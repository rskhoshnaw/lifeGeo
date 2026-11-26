namespace LifeDrop.Mobile.Visits;

// What the user types to send goods to another warehouse (plan step 5.61): the quantity of every product, sellable and damaged apart, never more than the sender holds. A product that is
// typed twice is the last number; zero or empty removes it. ClientRef stays the same for every retry of this transfer.
public sealed class TransferDraft
{
    private readonly Dictionary<(Guid Product, string Bucket), decimal> _lines = new();
    private readonly Dictionary<Guid, (decimal Sellable, decimal Damaged)> _stock = new();
    private readonly List<Guid> _order = new();

    public TransferDraft(Guid clientRef) { ClientRef = clientRef; }
    public Guid ClientRef { get; }
    public Guid? FromWarehouseId { get; private set; }
    public Guid? ToWarehouseId { get; set; }
    public string? Note { get; set; }

    // The sender and what it holds (from the stock list the phone already has). Changing the sender clears the lines.
    public void SetFrom(Guid warehouseId, IEnumerable<WarehouseStockItem> stock)
    {
        if (FromWarehouseId != warehouseId) { _lines.Clear(); }
        FromWarehouseId = warehouseId;
        if (ToWarehouseId == warehouseId) ToWarehouseId = null;
        _stock.Clear();
        _order.Clear();
        foreach (var s in stock.Where(s => s.WarehouseId == warehouseId && (s.Quantity > 0 || s.Damaged > 0)))
        {
            _stock[s.ProductId] = (s.Quantity, s.Damaged);
            _order.Add(s.ProductId);
        }
    }

    public IReadOnlyList<Guid> Products => _order;
    public decimal Available(Guid product, string bucket) => _stock.TryGetValue(product, out var s) ? (bucket == "damaged" ? s.Damaged : s.Sellable) : 0m;
    public decimal Quantity(Guid product, string bucket) => _lines.TryGetValue((product, bucket), out var q) ? q : 0m;

    // Text as typed. False when it is not a number, negative, has more than three decimals, or is more than the sender holds. Empty or 0 removes the line.
    public bool Set(Guid product, string bucket, string? text)
    {
        if (bucket is not ("sellable" or "damaged") || !_stock.ContainsKey(product)) return false;
        // Digits of any script and a comma or dot are accepted as in the order form; empty is zero.
        if (OrderDraft.ParseQuantity(text) is not { } quantity || quantity > Available(product, bucket)) return false;
        if (quantity == 0) _lines.Remove((product, bucket)); else _lines[(product, bucket)] = quantity;
        return true;
    }

    public int LineCount => _lines.Count;
    public bool CanSend => FromWarehouseId is not null && ToWarehouseId is not null && FromWarehouseId != ToWarehouseId && _lines.Count > 0;
    public IReadOnlyList<TransferLineInput> Lines() => _order.SelectMany(p => new[] { "sellable", "damaged" }.Where(b => _lines.ContainsKey((p, b))).Select(b => new TransferLineInput(p, _lines[(p, b)], b))).ToList();
}

// What arrived: starts with the full quantity of every line; the user lowers a line that came short.
public sealed class ReceiveDraft
{
    private readonly Dictionary<int, decimal> _sent;
    private readonly Dictionary<int, decimal> _arrived;

    public ReceiveDraft(IEnumerable<TransferLineItem> lines)
    {
        _sent = lines.ToDictionary(l => l.LineNo, l => l.Quantity);
        _arrived = new Dictionary<int, decimal>(_sent);
    }

    public decimal Arrived(int lineNo) => _arrived[lineNo];

    // False when not a number, negative, more than was sent, or an unknown line.
    public bool Set(int lineNo, string? text)
    {
        if (!_sent.ContainsKey(lineNo) || OrderDraft.ParseQuantity(text) is not { } value || value > _sent[lineNo]) return false;
        _arrived[lineNo] = value;
        return true;
    }

    public IReadOnlyDictionary<int, decimal> Lines() => _arrived;
    public bool AnyShort => _arrived.Any(a => a.Value < _sent[a.Key]);
}
