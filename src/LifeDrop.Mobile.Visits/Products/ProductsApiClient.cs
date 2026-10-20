using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile.Visits;

// One product as the catalogue page shows it (plan step 4.43). PricesHidden: the server hid the prices from this user (a ticked city or region), then BasePrice is 0 and must not be shown.
public sealed record ProductItem(Guid Id, string Code, string Name, string? NameCkb, string? NameAr, string? NameEn, string Unit, string Currency, decimal BasePrice,
    string? Specialty, string? Manufacturer, DateOnly? ExpiryDate, decimal? StockQty, bool InStock, string? Notes, string? PhotoKey, bool PricesHidden)
{
    public string DisplayName(string language)
    {
        var name = language switch { "ckb" => NameCkb, "ar" => NameAr, "en" => NameEn, _ => null };
        return string.IsNullOrWhiteSpace(name) ? Name : name;
    }
}

public enum StockFilter { All, InStock, OutOfStock }

// The catalogue page's filters, applied on the phone to the list the server sent: text (code or any name), one specialty, in or out of stock, one manufacturer.
public static class ProductFilter
{
    public static IReadOnlyList<ProductItem> Apply(IEnumerable<ProductItem> items, string? text, string? specialty, StockFilter stock, string? manufacturer, int max = 300)
    {
        var query = text?.Trim();
        return items.Where(p =>
                (string.IsNullOrEmpty(query) || Contains(p.Code, query) || Contains(p.Name, query) || Contains(p.NameCkb, query) || Contains(p.NameAr, query) || Contains(p.NameEn, query)) &&
                (string.IsNullOrEmpty(specialty) || p.Specialty == specialty) &&
                (stock == StockFilter.All || (stock == StockFilter.InStock) == p.InStock) &&
                (string.IsNullOrEmpty(manufacturer) || string.Equals(p.Manufacturer, manufacturer, StringComparison.OrdinalIgnoreCase)))
            .Take(max).ToList();
    }

    public static IReadOnlyList<string> Manufacturers(IEnumerable<ProductItem> items) =>
        items.Select(p => p.Manufacturer?.Trim()).Where(m => !string.IsNullOrEmpty(m)).Select(m => m!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<string> Specialties(IEnumerable<ProductItem> items) =>
        items.Select(p => p.Specialty).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

    private static bool Contains(string? value, string query) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}

// The product catalogue for a field user (plan step 4.43): GET /products. Online only; the list is what the server lets this user see (inactive products are left out, prices may be hidden).
public interface IProductsApiClient
{
    Task<(VisitActionResult Result, IReadOnlyList<ProductItem> Products)> GetProductsAsync(CancellationToken cancellationToken = default);
    // The address of a product photo, or null.
    string? PhotoUrl(string? photoKey);
}

public sealed class ProductsApiClient : IProductsApiClient
{
    private readonly HttpClient _httpClient;
    private readonly OrdersHttp _http;

    public ProductsApiClient(HttpClient httpClient, IAuthSession session)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _http = new OrdersHttp(httpClient, session ?? throw new ArgumentNullException(nameof(session)));
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<ProductItem> Products)> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<ProductDto>>(HttpMethod.Get, "/products", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<ProductItem>());
        if (items is null || items.Any(p => p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Code) || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Currency)))
            return (VisitActionResult.ServerError, Array.Empty<ProductItem>());
        return (result, items.Select(p => new ProductItem(p.Id, p.Code!, p.Name!, p.NameCkb, p.NameAr, p.NameEn, p.Unit ?? "", p.Currency!, p.BasePrice, p.Specialty, p.Manufacturer,
            p.ExpiryDate, p.StockQty, p.InStock, p.Notes, p.PhotoKey, p.PricesHidden)).ToList());
    }

    public string? PhotoUrl(string? photoKey) =>
        string.IsNullOrWhiteSpace(photoKey) || _httpClient.BaseAddress is null ? null : new Uri(_httpClient.BaseAddress, "/product-photos/" + Uri.EscapeDataString(photoKey)).ToString();

    private sealed record ProductDto(Guid Id, string? Code, string? Name, string? NameCkb, string? NameAr, string? NameEn, string? Unit, string? Currency, decimal BasePrice,
        string? Specialty, string? Manufacturer, DateOnly? ExpiryDate, decimal? StockQty, bool InStock, string? Notes, string? PhotoKey, bool PricesHidden);
}
