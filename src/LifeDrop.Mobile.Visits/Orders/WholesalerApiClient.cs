using System.Globalization;
using System.Net;

namespace LifeDrop.Mobile.Visits;

public sealed record WholesalerItem(Guid Id, string Name, int OpenOrders);
public sealed record WholesalerOrderLineItem(string ProductName, string Unit, decimal Quantity);
public sealed record WholesalerOrderItem(Guid Id, string Number, string Status, DateTimeOffset CreatedAt, string WholesalerName, string CustomerName, string? Phone, string? Address,
    string Currency, decimal Total, IReadOnlyList<WholesalerOrderLineItem> Lines);

// The orders of the wholesalers (plan step 5.6) for the wholesaler officer: their wholesalers, the open orders, and "dispatched". Online only.
public interface IWholesalerApiClient
{
    Task<(VisitActionResult Result, IReadOnlyList<WholesalerItem> Wholesalers)> GetWholesalersAsync(CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, IReadOnlyList<WholesalerOrderItem> Orders)> GetOrdersAsync(Guid? wholesalerId, CancellationToken cancellationToken = default);
    Task<VisitActionResult> DispatchAsync(Guid orderId, CancellationToken cancellationToken = default);
}

public sealed class WholesalerApiClient : IWholesalerApiClient
{
    private readonly OrdersHttp _http;

    public WholesalerApiClient(HttpClient httpClient, LifeDrop.Mobile.Auth.IAuthSession session) =>
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));

    public async Task<(VisitActionResult Result, IReadOnlyList<WholesalerItem> Wholesalers)> GetWholesalersAsync(CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<WholesalerDto>>(HttpMethod.Get, "/wholesaler/warehouses", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<WholesalerItem>());
        if (items is null || items.Any(w => w is null || w.Id == Guid.Empty)) return (VisitActionResult.ServerError, Array.Empty<WholesalerItem>());
        return (result, items.Select(w => new WholesalerItem(w.Id, w.Name ?? "", w.OpenOrders)).ToList());
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<WholesalerOrderItem> Orders)> GetOrdersAsync(Guid? wholesalerId, CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<OrderDto>>(HttpMethod.Get, "/wholesaler/orders?limit=200" + (wholesalerId is { } id ? "&warehouseId=" + id : ""), null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<WholesalerOrderItem>());
        if (items is null || items.Any(o => o is null || o.Id == Guid.Empty || string.IsNullOrWhiteSpace(o.Number) || string.IsNullOrWhiteSpace(o.Status) || o.Lines is null))
            return (VisitActionResult.ServerError, Array.Empty<WholesalerOrderItem>());
        return (result, items.Select(o => new WholesalerOrderItem(o.Id, o.Number!, o.Status!, o.CreatedAt, o.WholesalerName ?? "", o.CustomerName ?? "", o.Phone, o.Address,
            o.Currency ?? "", o.Total, o.Lines!.Select(l => new WholesalerOrderLineItem(l.ProductName ?? "", l.Unit ?? "", l.Quantity)).ToList())).ToList());
    }

    public async Task<VisitActionResult> DispatchAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/wholesaler/orders/{orderId}/dispatch", new { note = (string?)null }, cancellationToken)).Result;

    private sealed record WholesalerDto(Guid Id, string? Name, int OpenOrders);
    private sealed record LineDto(string? ProductName, string? Unit, decimal Quantity);
    private sealed record OrderDto(Guid Id, string? Number, string? Status, DateTimeOffset CreatedAt, string? WholesalerName, string? CustomerName, string? Phone, string? Address,
        string? Currency, decimal Total, List<LineDto>? Lines);
}

// The list the officer prints or saves as PDF (plan step 5.6): customer, address, product, quantity, date, price, phone. Plain HTML (the phone's
// print dialog turns it into a PDF); every text is escaped. The headings come from the caller so the report follows the app language.
public static class WholesalerReport
{
    public sealed record Headings(string Title, string Customer, string Address, string Product, string Quantity, string Date, string Price, string Phone);

    public static string BuildHtml(IReadOnlyList<WholesalerOrderItem> orders, Headings headings, bool rightToLeft)
    {
        var html = new System.Text.StringBuilder();
        html.Append("<!DOCTYPE html><html dir=\"").Append(rightToLeft ? "rtl" : "ltr").Append("\"><head><meta charset=\"utf-8\"><style>")
            .Append("body{font-family:sans-serif;font-size:12px}table{width:100%;border-collapse:collapse}th,td{border:1px solid #888;padding:4px;vertical-align:top;text-align:start}th{background:#eee}")
            .Append("</style></head><body><h2>").Append(E(headings.Title)).Append("</h2><table><tr>");
        foreach (var heading in new[] { headings.Customer, headings.Address, headings.Product, headings.Quantity, headings.Date, headings.Price, headings.Phone })
            html.Append("<th>").Append(E(heading)).Append("</th>");
        html.Append("</tr>");
        foreach (var order in orders)
        {
            html.Append("<tr><td>").Append(E(order.CustomerName)).Append("</td><td>").Append(E(order.Address)).Append("</td><td>")
                .Append(string.Join("<br>", order.Lines.Select(l => E(l.ProductName)))).Append("</td><td>")
                .Append(string.Join("<br>", order.Lines.Select(l => E(l.Quantity.ToString("0.###", CultureInfo.InvariantCulture) + " " + l.Unit)))).Append("</td><td>")
                .Append(order.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</td><td>")
                .Append(E(order.Total.ToString("0.###", CultureInfo.InvariantCulture) + " " + order.Currency)).Append("</td><td>").Append(E(order.Phone)).Append("</td></tr>");
        }
        return html.Append("</table></body></html>").ToString();
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
}
