namespace LifeDrop.Mobile.Visits;

// The return a user is writing (plan step 5.54): the products of the customer with a quantity, a reason and where the goods go. The rules live here so they are tested; the page only shows them.
// Damaged and expired goods go apart by default, every other reason goes back to the warehouse; the user may change it per line. A line that goes back needs a warehouse.
public sealed class ReturnDraft(Guid customerId)
{
    public static readonly string[] Reasons = ["damaged", "expired", "wrong_item", "customer_request", "other"];
    public static readonly string[] Destinations = ["warehouse", "damaged"];
    public const int MaxLines = 100;

    private sealed record Line(decimal Quantity, string Reason, string Destination);
    private readonly Dictionary<Guid, Line> _lines = [];

    public Guid CustomerId { get; } = customerId;
    public Guid? WarehouseId { get; set; }
    // The delivered order the goods come back from, or none (then today's price of the customer is used).
    public Guid? OrderId { get; private set; }
    public const int MaxPhotos = 3;

    // Another order (or none) is another list of products: the lines written so far are dropped.
    public void ChooseOrder(Guid? orderId)
    {
        if (OrderId == orderId) return;
        OrderId = orderId;
        _lines.Clear();
    }
    public string? Note { get; set; }
    // One reference for every retry of this return, so a lost answer never makes two.
    public Guid ClientRef { get; } = Guid.NewGuid();
    public IReadOnlyList<OrderWarehouse> Warehouses { get; private set; } = [];

    public static string DefaultDestination(string reason) => reason is "damaged" or "expired" ? "damaged" : "warehouse";

    public void Load(IReadOnlyList<OrderWarehouse> warehouses)
    {
        Warehouses = warehouses;
        if (WarehouseId is null && warehouses.Count == 1) WarehouseId = warehouses[0].Id;   // one warehouse: nothing to choose
    }

    // Typed text of a quantity (digits of any script, comma or dot); empty or 0 removes the line, text that is not a number is refused. A new line starts as "damaged".
    public bool SetQuantity(Guid productId, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || OrderDraft.ParseQuantity(text) is 0)
        {
            _lines.Remove(productId);
            return true;
        }
        if (OrderDraft.ParseQuantity(text) is not { } quantity || quantity <= 0 || quantity > 9_999_999m) return false;
        if (!_lines.TryGetValue(productId, out var line))
        {
            if (_lines.Count >= MaxLines) return false;
            line = new Line(quantity, "damaged", DefaultDestination("damaged"));
        }
        _lines[productId] = line with { Quantity = quantity };
        return true;
    }

    public decimal QuantityOf(Guid productId) => _lines.TryGetValue(productId, out var l) ? l.Quantity : 0;
    public string ReasonOf(Guid productId) => _lines.TryGetValue(productId, out var l) ? l.Reason : "damaged";
    public string DestinationOf(Guid productId) => _lines.TryGetValue(productId, out var l) ? l.Destination : DefaultDestination("damaged");

    // A new reason also sets the usual destination; only a line that exists can change.
    public bool SetReason(Guid productId, string reason)
    {
        if (!Reasons.Contains(reason) || !_lines.TryGetValue(productId, out var line)) return false;
        _lines[productId] = line with { Reason = reason, Destination = DefaultDestination(reason) };
        return true;
    }

    public bool SetDestination(Guid productId, string destination)
    {
        if (!Destinations.Contains(destination) || !_lines.TryGetValue(productId, out var line)) return false;
        _lines[productId] = line with { Destination = destination };
        return true;
    }

    public int LineCount => _lines.Count;
    // Every line is held in a warehouse (goods that go back to stock, or damaged / expired ones kept apart in the same warehouse), so any line needs one (step 5.56).
    public bool NeedsWarehouse => _lines.Count > 0;
    // Something to send, and a warehouse when a line goes back to one.
    public bool CanSend => _lines.Count > 0 && (!NeedsWarehouse || WarehouseId is not null);

    public IReadOnlyList<ReturnLineInput> Lines() => _lines.Select(p => new ReturnLineInput(p.Key, p.Value.Quantity, p.Value.Reason, p.Value.Destination)).ToList();
}
