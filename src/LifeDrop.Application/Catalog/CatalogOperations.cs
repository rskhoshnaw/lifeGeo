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

// Products and prices (plan step 2.1). One unit and one currency (IQD or USD, no conversion) per product. The general price applies
// everywhere; a city (a place of kind city) can have its own price between two days (both inclusive; no end = open), and a customer
// can get discounts (percentage or amount, on one product or on all). City prices of one product and city never overlap; a price or a
// discount is ended, not deleted.
public sealed record ProductDto(Guid Id, string Code, string Name, string? NameCkb, string? NameAr, string? NameEn, string Unit, string Currency,
    decimal BasePrice, string Status, DateTimeOffset UpdatedAt,
    // Plan step 4.28: specialty key, manufacturer, expiry day, quantity in stock (null = not tracked) and a note. InStock: not tracked or quantity above 0.
    string? Specialty = null, string? Manufacturer = null, DateOnly? ExpiryDate = null, decimal? StockQty = null, string? Notes = null,
    // Plan step 4.40: key of the photo (GET /product-photos/{key}); null = no photo.
    string? PhotoKey = null)
{
    public bool InStock => StockQty is null or > 0;
}

public sealed record ProductDetailsRequest(string? Specialty, string? Manufacturer, DateOnly? ExpiryDate, decimal? StockQty, string? Notes);

// Code is needed to create and ignored when editing (it never changes). Status: active (default) | inactive.
public sealed record SaveProductRequest(string? Code, string? Name, string? NameCkb, string? NameAr, string? NameEn, string? Unit, string? Currency,
    decimal? BasePrice, string? Status = null);

// A price of one product in one city (a place of kind city) between two days (both inclusive; no end = open).
public sealed record CityPriceDto(Guid Id, Guid ProductId, Guid CityId, string CityName, decimal Price, DateOnly ValidFrom, DateOnly? ValidTo);

// ValidFrom defaults to today (Iraq); ValidTo empty = open.
public sealed record CreateCityPriceRequest(Guid? CityId, decimal? Price, DateOnly? ValidFrom, DateOnly? ValidTo);

// The last valid day of a city price or a discount (not before its start, and not after an end it already has).
public sealed record EndRuleRequest(DateOnly? ValidTo);

// A discount of one customer: ProductId empty = all products (percent only); Kind: percent (0-100) | amount (per unit, in the product's currency).
public sealed record CustomerDiscountDto(Guid Id, Guid CustomerId, Guid? ProductId, string? ProductCode, string Kind, decimal Value, DateOnly ValidFrom, DateOnly? ValidTo);

public sealed record CreateDiscountRequest(Guid? ProductId, string? Kind, decimal? Value, DateOnly? ValidFrom, DateOnly? ValidTo);

// One discount that was applied, in the order it was applied.
public sealed record AppliedDiscountDto(Guid Id, string Kind, decimal Value, Guid? ProductId);

// What a customer pays for a product on a day. ListSource: base | city (the general price or the city's price); Price: after the discounts.
public sealed record EffectivePriceDto(Guid ProductId, string Code, string Name, string? NameCkb, string? NameAr, string? NameEn, string Unit,
    string Currency, decimal ListPrice, string ListSource, Guid? CityPriceId, IReadOnlyList<AppliedDiscountDto> Discounts, decimal Price,
    string? PhotoKey = null);

public sealed record ProductPhoto(string ContentType, byte[] Data);

public enum CatalogResult { Success, Forbidden, NotFound, InvalidData, Conflict }

// What the store reads for one customer and day: every active product with the general price and the city price that applies,
// and the discounts that are valid. The price itself is worked out by PriceCalculator.
public sealed record ProductPriceRow(ProductDto Product, decimal? CityPrice, Guid? CityPriceId);
public sealed record DiscountRow(Guid Id, Guid? ProductId, string Kind, decimal Value, DateTimeOffset CreatedAt);
public sealed record PricingData(IReadOnlyList<ProductPriceRow> Products, IReadOnlyList<DiscountRow> Discounts);

public interface IProductStore
{
    Task<IReadOnlyList<ProductDto>> ListAsync(TenantId tenantId, bool includeInactive, string? query, CancellationToken cancellationToken);
    // Null when the code exists already.
    Task<Guid?> CreateAsync(TenantId tenantId, UserId actorId, SaveProductRequest product, CancellationToken cancellationToken);
    // False when the product does not exist.
    Task<bool> UpdateAsync(TenantId tenantId, UserId actorId, Guid productId, SaveProductRequest product, CancellationToken cancellationToken);

    // Specialty, manufacturer, expiry day, stock and note (plan step 4.28); null clears. False when the product does not exist. Audit product.details_changed.
    Task<bool> SetDetailsAsync(TenantId tenantId, UserId actorId, Guid productId, ProductDetailsRequest details, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // Plan step 4.40. Set: replaces the photo with a new random key (false = no such product); audit product.photo_changed. Remove: false = no photo. Get: by key, any company.
    Task<bool> SetPhotoAsync(TenantId tenantId, UserId actorId, Guid productId, ProductPhoto photo, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<bool> RemovePhotoAsync(TenantId tenantId, UserId actorId, Guid productId, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<ProductPhoto?> GetPhotoAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<IReadOnlyList<CityPriceDto>?> ListCityPricesAsync(TenantId tenantId, Guid productId, CancellationToken cancellationToken);
    // NotFound: product or city missing. InvalidData: the place is not a city. Conflict: the days overlap another price of the same product and city.
    Task<(CatalogResult Result, Guid? Id)> CreateCityPriceAsync(TenantId tenantId, UserId actorId, Guid productId, CreateCityPriceRequest request, DateOnly validFrom,
        CancellationToken cancellationToken);
    // NotFound: no such price. InvalidData: the end is before the start or after an end it has.
    Task<CatalogResult> EndCityPriceAsync(TenantId tenantId, UserId actorId, Guid cityPriceId, DateOnly validTo, CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerDiscountDto>> ListDiscountsAsync(TenantId tenantId, Guid customerId, CancellationToken cancellationToken);
    // NotFound: customer or product missing.
    Task<(CatalogResult Result, Guid? Id)> CreateDiscountAsync(TenantId tenantId, UserId actorId, Guid customerId, CreateDiscountRequest request, DateOnly validFrom,
        CancellationToken cancellationToken);
    Task<CatalogResult> EndDiscountAsync(TenantId tenantId, UserId actorId, Guid discountId, DateOnly validTo, CancellationToken cancellationToken);

    Task<PricingData> GetPricingAsync(TenantId tenantId, Guid customerId, DateOnly day, CancellationToken cancellationToken);
}

// Worked out here, not in SQL: list price (the city price when there is one, else the general price), then every valid discount oldest
// first, each on the price left: a percentage takes that share, an amount takes that much per unit; never below 0; three decimals.
public static class PriceCalculator
{
    public static (decimal Price, IReadOnlyList<AppliedDiscountDto> Applied) Apply(decimal listPrice, Guid productId, IEnumerable<DiscountRow> discounts)
    {
        var price = listPrice;
        var applied = new List<AppliedDiscountDto>();
        foreach (var d in discounts.Where(d => d.ProductId is null || d.ProductId == productId).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id))
        {
            if (d.Kind == "percent") price -= price * d.Value / 100m;
            else if (d.ProductId is not null) price -= d.Value;   // an amount belongs to one product
            else continue;
            price = Math.Max(0m, decimal.Round(price, 3, MidpointRounding.AwayFromZero));
            applied.Add(new AppliedDiscountDto(d.Id, d.Kind, d.Value, d.ProductId));
        }
        return (price, applied);
    }
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

    public const int MaxManufacturerLength = 100;
    public const int MaxNotesLength = 500;
    public const int MaxPhotoBytes = 1_048_576;

    // JPEG, PNG or WEBP by the first bytes (the header the phone sent is not trusted); up to 1 MB. InvalidData otherwise.
    public static string? PhotoTypeOf(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxPhotoBytes) return null;
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    public async Task<CatalogResult> SetPhotoAsync(TenantId tenantId, UserId actorId, Guid productId, byte[] data, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken)) return CatalogResult.Forbidden;
        var type = PhotoTypeOf(data);
        if (productId == Guid.Empty || type is null) return CatalogResult.InvalidData;
        return await store.SetPhotoAsync(tenantId, actorId, productId, new ProductPhoto(type, data), cancellationToken) ? CatalogResult.Success : CatalogResult.NotFound;
    }

    public async Task<CatalogResult> RemovePhotoAsync(TenantId tenantId, UserId actorId, Guid productId, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken)) return CatalogResult.Forbidden;
        if (productId == Guid.Empty) return CatalogResult.InvalidData;
        return await store.RemovePhotoAsync(tenantId, actorId, productId, cancellationToken) ? CatalogResult.Success : CatalogResult.NotFound;
    }

    // Public: the key is 32 random hex characters.
    public Task<ProductPhoto?> GetPhotoAsync(string key, CancellationToken cancellationToken) =>
        key.Length == 32 && key.All(Uri.IsHexDigit) ? store.GetPhotoAsync(key.ToLowerInvariant(), cancellationToken) : Task.FromResult<ProductPhoto?>(null);

    // catalog.manage; an empty text or null clears a field. The quantity has up to three decimals.
    public async Task<CatalogResult> SetDetailsAsync(TenantId tenantId, UserId actorId, Guid productId, ProductDetailsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken)) return CatalogResult.Forbidden;
        string? Opt(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var specialty = Opt(request.Specialty);
        var manufacturer = Opt(request.Manufacturer);
        var notes = Opt(request.Notes);
        if (productId == Guid.Empty || (specialty is not null && !DoctorSpecialties.Keys.Contains(specialty)) || manufacturer?.Length > MaxManufacturerLength ||
            notes?.Length > MaxNotesLength || request.StockQty is { } qty && !(qty >= 0 && qty <= MaxPrice && decimal.Round(qty, 3) == qty))
            return CatalogResult.InvalidData;
        return await store.SetDetailsAsync(tenantId, actorId, productId, new ProductDetailsRequest(specialty, manufacturer, request.ExpiryDate, request.StockQty, notes), cancellationToken)
            ? CatalogResult.Success : CatalogResult.NotFound;
    }

    public async Task<(CatalogResult Result, IReadOnlyList<CityPriceDto>? Items)> ListCityPricesAsync(TenantId tenantId, UserId actorId, Guid productId, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogRead, cancellationToken)) return (CatalogResult.Forbidden, null);
        var items = productId == Guid.Empty ? null : await store.ListCityPricesAsync(tenantId, productId, cancellationToken);
        return items is null ? (CatalogResult.NotFound, null) : (CatalogResult.Success, items);
    }

    public async Task<(CatalogResult Result, Guid? Id)> CreateCityPriceAsync(TenantId tenantId, UserId actorId, Guid productId, CreateCityPriceRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken)) return (CatalogResult.Forbidden, null);
        var from = request.ValidFrom ?? IraqCalendar.Today(clock);
        if (productId == Guid.Empty || request.CityId is null || request.CityId == Guid.Empty || !ValidPrice(request.Price) || request.ValidTo is { } to && to < from)
            return (CatalogResult.InvalidData, null);
        return await store.CreateCityPriceAsync(tenantId, actorId, productId, request, from, cancellationToken);
    }

    public async Task<CatalogResult> EndCityPriceAsync(TenantId tenantId, UserId actorId, Guid cityPriceId, EndRuleRequest request, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken)) return CatalogResult.Forbidden;
        if (cityPriceId == Guid.Empty || request.ValidTo is null) return CatalogResult.InvalidData;
        return await store.EndCityPriceAsync(tenantId, actorId, cityPriceId, request.ValidTo.Value, cancellationToken);
    }

    // Discounts of a customer: catalog.read and a customer inside the caller's customers.read scope (else NotFound).
    public async Task<(CatalogResult Result, IReadOnlyList<CustomerDiscountDto>? Items)> ListDiscountsAsync(TenantId tenantId, UserId actorId, Guid customerId,
        CancellationToken cancellationToken)
    {
        var (result, _) = await FindCustomerAsync(tenantId, actorId, customerId, cancellationToken);
        return result == CatalogResult.Success ? (result, await store.ListDiscountsAsync(tenantId, customerId, cancellationToken)) : (result, null);
    }

    public async Task<(CatalogResult Result, Guid? Id)> CreateDiscountAsync(TenantId tenantId, UserId actorId, Guid customerId, CreateDiscountRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken)) return (CatalogResult.Forbidden, null);
        var from = request.ValidFrom ?? IraqCalendar.Today(clock);
        var percent = request.Kind == "percent";
        if (customerId == Guid.Empty || request.ProductId == Guid.Empty || request.Kind is not ("percent" or "amount") ||
            request.Value is not > 0 || decimal.Round(request.Value.Value, 3) != request.Value || request.Value > (percent ? 100m : MaxPrice) ||
            (!percent && request.ProductId is null) || request.ValidTo is { } to && to < from)
            return (CatalogResult.InvalidData, null);
        return await store.CreateDiscountAsync(tenantId, actorId, customerId, request, from, cancellationToken);
    }

    public async Task<CatalogResult> EndDiscountAsync(TenantId tenantId, UserId actorId, Guid discountId, EndRuleRequest request, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogManage, cancellationToken)) return CatalogResult.Forbidden;
        if (discountId == Guid.Empty || request.ValidTo is null) return CatalogResult.InvalidData;
        return await store.EndDiscountAsync(tenantId, actorId, discountId, request.ValidTo.Value, cancellationToken);
    }

    // The prices a customer gets on a day (default today in Iraq): the general or city price, then the discounts. Needs catalog.read and
    // a customer inside the caller's customers.read scope. Orders take prices from here, never from the phone.
    public async Task<(CatalogResult Result, IReadOnlyList<EffectivePriceDto>? Items)> PricesForCustomerAsync(TenantId tenantId, UserId actorId, Guid customerId, DateOnly? day,
        CancellationToken cancellationToken)
    {
        var (result, _) = await FindCustomerAsync(tenantId, actorId, customerId, cancellationToken);
        if (result != CatalogResult.Success) return (result, null);
        var data = await store.GetPricingAsync(tenantId, customerId, day ?? IraqCalendar.Today(clock), cancellationToken);
        var items = data.Products.Select(row =>
        {
            var list = row.CityPrice ?? row.Product.BasePrice;
            var (price, applied) = PriceCalculator.Apply(list, row.Product.Id, data.Discounts);
            var p = row.Product;
            return new EffectivePriceDto(p.Id, p.Code, p.Name, p.NameCkb, p.NameAr, p.NameEn, p.Unit, p.Currency, list, row.CityPrice is null ? "base" : "city", row.CityPriceId, applied, price, p.PhotoKey);
        }).ToList();
        return (CatalogResult.Success, items);
    }

    private async Task<(CatalogResult Result, CustomerDto? Customer)> FindCustomerAsync(TenantId tenantId, UserId actorId, Guid customerId, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, PermissionKeys.CatalogRead, cancellationToken)) return (CatalogResult.Forbidden, null);
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken);
        if (scope is null) return (CatalogResult.Forbidden, null);
        if (customerId == Guid.Empty) return (CatalogResult.InvalidData, null);
        var customer = await customers.GetByIdAsync(tenantId, new CustomerId(customerId), scope, cancellationToken);
        return customer is null ? (CatalogResult.NotFound, null) : (CatalogResult.Success, customer);
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
