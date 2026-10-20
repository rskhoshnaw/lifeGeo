using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Catalog;

// Prices hidden in some places (plan step 4.42). A city or a region is ticked "hide prices" by a manager. A user with the restriction catalog.prices.restricted (the field roles) gets no
// prices when the customer's city is ticked or the user's own region is ticked: the amounts are 0 and PricesHidden is true. Office users are never restricted.
public interface IPriceVisibilityStore
{
    // True when the region is ticked.
    Task<bool> RegionHidesPricesAsync(TenantId tenantId, Guid regionId, CancellationToken cancellationToken);
    // Of these customers, the ones whose city (the place above their place, or the place itself) is ticked.
    Task<IReadOnlySet<Guid>> CustomersInHiddenCitiesAsync(TenantId tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);
    // false: no such city (the place is missing or not a city) / region. Audit place.prices_visibility_changed / region.prices_visibility_changed.
    Task<bool> SetCityHidesPricesAsync(TenantId tenantId, UserId actorId, Guid placeId, bool hide, CancellationToken cancellationToken);
    Task<bool> SetRegionHidesPricesAsync(TenantId tenantId, UserId actorId, Guid regionId, bool hide, CancellationToken cancellationToken);
}

public sealed record SetHidePricesRequest(bool? HidePrices);

public enum PriceVisibilityResult { Success, Forbidden, InvalidData, NotFound }

public sealed class PriceVisibility(IPriceVisibilityStore store, IPermissionStore permissions)
{
    public const string RestrictedKey = "catalog.prices.restricted";

    // Of these customers, the ones the caller must not see prices for. Empty when the caller is not restricted.
    public async Task<IReadOnlySet<Guid>> HiddenForAsync(TenantId tenantId, UserId actorId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty || !await permissions.HasPermissionAsync(tenantId, actorId, RestrictedKey, cancellationToken)) return new HashSet<Guid>();
        if (await OwnRegionHidesAsync(tenantId, actorId, cancellationToken)) return customerIds.ToHashSet();
        return customerIds.Count == 0 ? new HashSet<Guid>() : await store.CustomersInHiddenCitiesAsync(tenantId, customerIds, cancellationToken);
    }

    // For a list without a customer (the product list): only the caller's own region counts.
    public async Task<bool> HiddenWithoutCustomerAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        !tenantId.IsEmpty && !actorId.IsEmpty && await permissions.HasPermissionAsync(tenantId, actorId, RestrictedKey, cancellationToken) &&
        await OwnRegionHidesAsync(tenantId, actorId, cancellationToken);

    private async Task<bool> OwnRegionHidesAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        var (_, region) = await permissions.GetUserRegionAsync(tenantId, actorId, cancellationToken);
        return region is { } r && await store.RegionHidesPricesAsync(tenantId, r, cancellationToken);
    }

    public async Task<PriceVisibilityResult> SetCityAsync(TenantId tenantId, UserId actorId, Guid placeId, SetHidePricesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (tenantId.IsEmpty || actorId.IsEmpty || !await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken)) return PriceVisibilityResult.Forbidden;
        if (placeId == Guid.Empty || request.HidePrices is null) return PriceVisibilityResult.InvalidData;
        return await store.SetCityHidesPricesAsync(tenantId, actorId, placeId, request.HidePrices.Value, cancellationToken) ? PriceVisibilityResult.Success : PriceVisibilityResult.NotFound;
    }

    public async Task<PriceVisibilityResult> SetRegionAsync(TenantId tenantId, UserId actorId, Guid regionId, SetHidePricesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (tenantId.IsEmpty || actorId.IsEmpty || !await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.RegionsManage, cancellationToken)) return PriceVisibilityResult.Forbidden;
        if (regionId == Guid.Empty || request.HidePrices is null) return PriceVisibilityResult.InvalidData;
        return await store.SetRegionHidesPricesAsync(tenantId, actorId, regionId, request.HidePrices.Value, cancellationToken) ? PriceVisibilityResult.Success : PriceVisibilityResult.NotFound;
    }

    public static ProductDto Mask(ProductDto product) => product with { BasePrice = 0, PricesHidden = true };

    public static EffectivePriceDto Mask(EffectivePriceDto price) => price with { ListPrice = 0, Discounts = [], Price = 0, PricesHidden = true };
}
