using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Catalog;

// Products and prices (plan step 2.1). One unit and one currency (IQD or USD, no conversion) per product. A price rule applies to
// one customer or one region between two days (both inclusive; no end = open). For a customer on a day: its own rule, else its
// region's rule, else the base price. Rules of one product and target never overlap; a rule is ended, not deleted.
public sealed record ProductDto(Guid Id, string Code, string Name, string? NameCkb, string? NameAr, string? NameEn, string Unit, string Currency,
    decimal BasePrice, string Status, DateTimeOffset UpdatedAt);

// Code is needed to create and ignored when editing (it never changes). Status: active (default) | inactive.
public sealed record SaveProductRequest(string? Code, string? Name, string? NameCkb, string? NameAr, string? NameEn, string? Unit, string? Currency,
    decimal? BasePrice, string? Status = null);

// Scope: customer | region. TargetName: the customer's code and name, or the region's name.
public sealed record PriceRuleDto(Guid Id, Guid ProductId, string Scope, Guid? CustomerId, Guid? RegionId, string? TargetName, decimal Price,
    DateOnly ValidFrom, DateOnly? ValidTo);

// Exactly one of CustomerId / RegionId. ValidFrom defaults to today (Iraq); ValidTo empty = open.
public sealed record CreatePriceRuleRequest(Guid? CustomerId, Guid? RegionId, decimal? Price, DateOnly? ValidFrom, DateOnly? ValidTo);

// The last valid day of a rule (not before its start, and not after an end it already has).
public sealed record EndPriceRuleRequest(DateOnly? ValidTo);

// Source: customer | region | base. RuleId: the rule that gave the price.
public sealed record EffectivePriceDto(Guid ProductId, string Code, string Name, string? NameCkb, string? NameAr, string? NameEn, string Unit,
    string Currency, decimal Price, string Source, Guid? RuleId);

public enum CatalogResult { Success, Forbidden, NotFound, InvalidData, Conflict }

public interface IProductStore
{
    Task<IReadOnlyList<ProductDto>> ListAsync(TenantId tenantId, bool includeInactive, string? query, CancellationToken cancellationToken);
    // Null when the code exists already.
    Task<Guid?> CreateAsync(TenantId tenantId, UserId actorId, SaveProductRequest product, CancellationToken cancellationToken);
    // False when the product does not exist.
    Task<bool> UpdateAsync(TenantId tenantId, UserId actorId, Guid productId, SaveProductRequest product, CancellationToken cancellationToken);

    Task<IReadOnlyList<PriceRuleDto>?> ListRulesAsync(TenantId tenantId, Guid productId, CancellationToken cancellationToken);
    // NotFound: product, customer or region missing. Conflict: the days overlap another rule of the same product and target.
    Task<(CatalogResult Result, Guid? Id)> CreateRuleAsync(TenantId tenantId, UserId actorId, Guid productId, CreatePriceRuleRequest rule, DateOnly validFrom,
        CancellationToken cancellationToken);
    // NotFound: no such rule. InvalidData: the end is before the start or after an end the rule has.
    Task<CatalogResult> EndRuleAsync(TenantId tenantId, UserId actorId, Guid ruleId, DateOnly validTo, CancellationToken cancellationToken);

    // The price of every active product for one customer on one day.
    Task<IReadOnlyList<EffectivePriceDto>> EffectivePricesAsync(TenantId tenantId, Guid customerId, Guid? customerRegionId, DateOnly day,
        CancellationToken cancellationToken);
}

public sealed partial class CatalogService(IProductStore store, IPermissionStore permissions, ICustomerStore customers, TimeProvider clock)
{
    public const int MaxCodeLength = 40;
    public const int MaxNameLength = 160;
    public const int MaxUnitLength = 40;
    public const decimal MaxPrice = 999_999_999_999m;
    public static readonly string[] Currencies = ["IQD", "USD"];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex CodePattern();

    public async Task<(CatalogResult Result, IReadOnlyList<ProductDto> Items)> ListAsync(TenantId tenantId, UserId actorId, bool includeInactive, string? query,
        CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogRead, cancellationToken)) return (CatalogResult.Forbidden, Array.Empty<ProductDto>());
        // Only managers see inactive products.
        var inactive = includeInactive && await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken);
        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        if (text is { Length: > MaxNameLength }) return (CatalogResult.InvalidData, Array.Empty<ProductDto>());
        return (CatalogResult.Success, await store.ListAsync(tenantId, inactive, text, cancellationToken));
    }

    public async Task<(CatalogResult Result, Guid? Id)> CreateAsync(TenantId tenantId, UserId actorId, SaveProductRequest request, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken)) return (CatalogResult.Forbidden, null);
        var code = request.Code?.Trim();
        if (code is null || code.Length is 0 or > MaxCodeLength || !CodePattern().IsMatch(code) || Clean(request, out var clean) is { } error && error != CatalogResult.Success)
            return (CatalogResult.InvalidData, null);
        var id = await store.CreateAsync(tenantId, actorId, clean! with { Code = code }, cancellationToken);
        return id is null ? (CatalogResult.Conflict, null) : (CatalogResult.Success, id);
    }

    public async Task<CatalogResult> UpdateAsync(TenantId tenantId, UserId actorId, Guid productId, SaveProductRequest request, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken)) return CatalogResult.Forbidden;
        if (productId == Guid.Empty || Clean(request, out var clean) is { } error && error != CatalogResult.Success) return CatalogResult.InvalidData;
        return await store.UpdateAsync(tenantId, actorId, productId, clean!, cancellationToken) ? CatalogResult.Success : CatalogResult.NotFound;
    }

    public async Task<(CatalogResult Result, IReadOnlyList<PriceRuleDto>? Items)> ListRulesAsync(TenantId tenantId, UserId actorId, Guid productId, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogRead, cancellationToken)) return (CatalogResult.Forbidden, null);
        var items = productId == Guid.Empty ? null : await store.ListRulesAsync(tenantId, productId, cancellationToken);
        return items is null ? (CatalogResult.NotFound, null) : (CatalogResult.Success, items);
    }

    public async Task<(CatalogResult Result, Guid? Id)> CreateRuleAsync(TenantId tenantId, UserId actorId, Guid productId, CreatePriceRuleRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken)) return (CatalogResult.Forbidden, null);
        var from = request.ValidFrom ?? IraqCalendar.Today(clock);
        if (productId == Guid.Empty || (request.CustomerId is null) == (request.RegionId is null) || request.CustomerId == Guid.Empty || request.RegionId == Guid.Empty ||
            !ValidPrice(request.Price) || request.ValidTo is { } to && to < from)
            return (CatalogResult.InvalidData, null);
        return await store.CreateRuleAsync(tenantId, actorId, productId, request, from, cancellationToken);
    }

    public async Task<CatalogResult> EndRuleAsync(TenantId tenantId, UserId actorId, Guid ruleId, EndPriceRuleRequest request, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken)) return CatalogResult.Forbidden;
        if (ruleId == Guid.Empty || request.ValidTo is null) return CatalogResult.InvalidData;
        return await store.EndRuleAsync(tenantId, actorId, ruleId, request.ValidTo.Value, cancellationToken);
    }

    // The prices a customer gets on a day (default today in Iraq): needs catalog.read and a customer inside the caller's customers.read scope.
    public async Task<(CatalogResult Result, IReadOnlyList<EffectivePriceDto>? Items)> PricesForCustomerAsync(TenantId tenantId, UserId actorId, Guid customerId, DateOnly? day,
        CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogRead, cancellationToken)) return (CatalogResult.Forbidden, null);
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken);
        if (scope is null) return (CatalogResult.Forbidden, null);
        if (customerId == Guid.Empty) return (CatalogResult.InvalidData, null);
        var customer = await customers.GetByIdAsync(tenantId, new CustomerId(customerId), scope, cancellationToken);
        if (customer is null) return (CatalogResult.NotFound, null);
        return (CatalogResult.Success, await store.EffectivePricesAsync(tenantId, customerId, customer.RegionId, day ?? IraqCalendar.Today(clock), cancellationToken));
    }

    private async Task<bool> CanAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        !tenantId.IsEmpty && !actorId.IsEmpty && await permissions.GetAccessScopeAsync(tenantId, actorId, permission, cancellationToken) is not null;

    private static bool ValidPrice(decimal? price) => price is >= 0 and <= MaxPrice && decimal.Round(price.Value, 3) == price.Value;

    // The fields every save needs, trimmed. Success with the cleaned request, or InvalidData.
    private static CatalogResult? Clean(SaveProductRequest request, out SaveProductRequest? clean)
    {
        clean = null;
        string? Opt(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var name = request.Name?.Trim();
        var unit = request.Unit?.Trim();
        var status = request.Status ?? "active";
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || string.IsNullOrEmpty(unit) || unit.Length > MaxUnitLength ||
            request.Currency is null || !Currencies.Contains(request.Currency) || !ValidPrice(request.BasePrice) || status is not ("active" or "inactive") ||
            Opt(request.NameCkb)?.Length > MaxNameLength || Opt(request.NameAr)?.Length > MaxNameLength || Opt(request.NameEn)?.Length > MaxNameLength)
            return CatalogResult.InvalidData;
        clean = request with { Name = name, Unit = unit, NameCkb = Opt(request.NameCkb), NameAr = Opt(request.NameAr), NameEn = Opt(request.NameEn), Status = status };
        return CatalogResult.Success;
    }
}
