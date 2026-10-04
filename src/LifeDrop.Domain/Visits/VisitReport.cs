namespace LifeDrop.Domain.Visits;

// Base visit report (PRD §5.3): an explicit outcome and a meaningful note. Outcome values are a versioned
// contract shared with mobile. "Order placed" is intentionally absent until real orders exist.
public enum VisitOutcome { ProductsPresented, OrderIntent, FollowUpNeeded, NoInterest, Other }

public sealed record VisitReport(VisitOutcome Outcome, string Note)
{
    public const int ContractVersion = 1;
    public const int MinNoteLength = 3;
    public const int MaxNoteLength = 2000;

    // Rejects unknown outcomes, unknown contract versions and notes that are blank, too short or too long,
    // or contain no letters or digits.
    public static bool TryCreate(string? outcome, string? note, int? contractVersion, out VisitReport? report)
    {
        report = null;
        if ((contractVersion ?? ContractVersion) != ContractVersion) return false;
        if (!TryParseOutcome(outcome, out var parsed)) return false;
        var trimmed = note?.Trim();
        if (trimmed is null || trimmed.Length < MinNoteLength || trimmed.Length > MaxNoteLength) return false;
        if (!trimmed.Any(char.IsLetterOrDigit)) return false;
        report = new VisitReport(parsed, trimmed);
        return true;
    }

    public static bool TryParseOutcome(string? value, out VisitOutcome outcome)
    {
        outcome = VisitOutcome.Other;
        switch (value)
        {
            case "products_presented": outcome = VisitOutcome.ProductsPresented; return true;
            case "order_intent": outcome = VisitOutcome.OrderIntent; return true;
            case "follow_up_needed": outcome = VisitOutcome.FollowUpNeeded; return true;
            case "no_interest": outcome = VisitOutcome.NoInterest; return true;
            case "other": outcome = VisitOutcome.Other; return true;
            default: return false;
        }
    }

    public string OutcomeContract => Outcome switch
    {
        VisitOutcome.ProductsPresented => "products_presented",
        VisitOutcome.OrderIntent => "order_intent",
        VisitOutcome.FollowUpNeeded => "follow_up_needed",
        VisitOutcome.NoInterest => "no_interest",
        _ => "other"
    };
}
