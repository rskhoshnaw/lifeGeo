using LifeDrop.Mobile.Offline;
using System.Globalization;

namespace LifeDrop.Mobile.Visits;

// The order a field user is putting together (plan step 2.2): the products the server prices for this customer, a quantity per product,
// and one warehouse chosen by the user. Prices are only shown; the server sets them again when it accepts the order. The ClientRef
// stays the same until the order is placed, so pressing "place" again after a lost answer returns the same order.
public sealed class OrderDraft(Guid customerId, Guid? visitId = null)
{
    public const int MaxLines = 100;
    public const decimal MaxQuantity = 100_000m;
    public const int MaxNoteLength = 500;

    private readonly Dictionary<Guid, decimal> _quantities = [];

    public Guid CustomerId { get; } = customerId;
    public Guid? VisitId { get; } = visitId;
    public Guid ClientRef { get; private set; } = Guid.NewGuid();
    public IReadOnlyList<PricedProduct> Products { get; private set; } = Array.Empty<PricedProduct>();
    public IReadOnlyList<OrderWarehouse> Warehouses { get; private set; } = Array.Empty<OrderWarehouse>();
    public Guid? WarehouseId { get; set; }
    public string? Note { get; set; }
    public PlacedOrder? Placed { get; private set; }
    // False while the lists come from the phone's memory (no internet): the prices are unknown and the server sets them when the order arrives (plan step 2.13).
    public bool PricesKnown { get; set; } = true;
    // The order is kept on the phone to be sent later: the draft offers no second place.
    public bool Queued { get; set; }

    public void Load(IReadOnlyList<PricedProduct> products, IReadOnlyList<OrderWarehouse> warehouses)
    {
        Products = products;
        Warehouses = warehouses;
        _quantities.Keys.Where(id => products.All(p => p.ProductId != id)).ToList().ForEach(id => _quantities.Remove(id));
        if (WarehouseId is null && warehouses.Count == 1) WarehouseId = warehouses[0].Id;   // nothing to choose
    }

    // The text a user typed in the quantity box: digits of any script (Arabic-Indic and Persian digits are common on these phones), a dot or
    // a comma as decimal mark. Empty or 0 removes the line. Returns false (and keeps the old value) for text that is not a quantity.
    public bool SetQuantity(Guid productId, string? text)
    {
        if (Products.All(p => p.ProductId != productId)) return false;
        var value = ParseQuantity(text);
        if (value is null) return false;
        if (value == 0) _quantities.Remove(productId);
        else if (_quantities.ContainsKey(productId) || _quantities.Count < MaxLines) _quantities[productId] = value.Value;
        else return false;
        return true;
    }

    public decimal QuantityOf(Guid productId) => _quantities.GetValueOrDefault(productId);

    public static decimal? ParseQuantity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        var ascii = new string(text.Trim().Select(c => c switch
        {
            >= '٠' and <= '٩' => (char)('0' + (c - '٠')),   // Arabic-Indic
            >= '۰' and <= '۹' => (char)('0' + (c - '۰')),   // Persian
            '٫' or ',' => '.',
            _ => c
        }).ToArray());
        if (!decimal.TryParse(ascii, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        return value < 0 || value > MaxQuantity || decimal.Round(value, 3) != value ? null : value;
    }

    public IReadOnlyList<(PricedProduct Product, decimal Quantity, decimal LineTotal)> Lines =>
        Products.Where(p => _quantities.ContainsKey(p.ProductId)).Select(p => (p, _quantities[p.ProductId], Math.Round(p.Price * _quantities[p.ProductId], 3))).ToList();

    // Plan step 4.43: the server hid the prices from this user; no price or total is shown.
    public bool PricesHidden => Products.Any(p => p.PricesHidden);

    public bool MixedCurrency => Lines.Select(l => l.Product.Currency).Distinct().Count() > 1;
    public string Currency => Lines.Count == 0 ? "" : Lines[0].Product.Currency;
    public decimal Total => Lines.Sum(l => l.LineTotal);

    public bool CanPlace => Placed is null && !Queued && Lines.Count > 0 && !MixedCurrency && WarehouseId is { } id && Warehouses.Any(w => w.Id == id) && (Note?.Trim().Length ?? 0) <= MaxNoteLength;

    // Products matching a search text (code or any name), at most max; everything when the text is empty.
    public IReadOnlyList<PricedProduct> Find(string? text, string language, int max = 200)
    {
        var query = text?.Trim();
        var found = string.IsNullOrEmpty(query)
            ? Products
            : Products.Where(p => Contains(p.Code, query) || Contains(p.Name, query) || Contains(p.NameCkb, query) || Contains(p.NameAr, query) || Contains(p.NameEn, query)).ToList();
        return found.Take(max).ToList();
    }

    private static bool Contains(string? value, string query) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    public async Task<VisitActionResult> PlaceAsync(IOrdersApiClient api, CancellationToken cancellationToken = default)
    {
        if (!CanPlace) return VisitActionResult.InvalidState;
        var note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
        var (result, order) = await api.PlaceAsync(CustomerId, WarehouseId!.Value, VisitId, ClientRef, note,
            Lines.Select(l => new OrderLineInput(l.Product.ProductId, l.Quantity)).ToList(), cancellationToken);
        if (result == VisitActionResult.Success) Placed = order;
        return result;
    }

    // What is needed to send this order later (the same reference, so a retry never makes a second order).
    public OrderPayload ToPayload() => new(CustomerId, WarehouseId!.Value, VisitId, ClientRef, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
        Lines.Select(l => new OrderLineInput(l.Product.ProductId, l.Quantity)).ToList());

    // After a successful order: an empty draft with a new reference (the warehouse stays).
    public void StartNext()
    {
        _quantities.Clear();
        Note = null;
        Placed = null;
        Queued = false;
        ClientRef = Guid.NewGuid();
    }
}
