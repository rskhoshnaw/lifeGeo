namespace LifeDrop.Domain.Visits;

// Contract values are stored as text: sales, scientific, follow_up, other.
public enum VisitType { Sales, Scientific, FollowUp, Other }

// Contract values are stored as text: high, normal, low.
public enum VisitPriority { High, Normal, Low }

public static class VisitPlanningContract
{
    public static string ToContract(this VisitType type) => type switch
    {
        VisitType.Sales => "sales",
        VisitType.Scientific => "scientific",
        VisitType.FollowUp => "follow_up",
        _ => "other"
    };

    public static string ToContract(this VisitPriority priority) => priority switch
    {
        VisitPriority.High => "high",
        VisitPriority.Low => "low",
        _ => "normal"
    };

    public static bool TryParseType(string? value, out VisitType type)
    {
        type = VisitType.Sales;
        switch (value)
        {
            case null: return true;
            case "sales": type = VisitType.Sales; return true;
            case "scientific": type = VisitType.Scientific; return true;
            case "follow_up": type = VisitType.FollowUp; return true;
            case "other": type = VisitType.Other; return true;
            default: return false;
        }
    }

    public static bool TryParsePriority(string? value, out VisitPriority priority)
    {
        priority = VisitPriority.Normal;
        switch (value)
        {
            case null: return true;
            case "high": priority = VisitPriority.High; return true;
            case "normal": priority = VisitPriority.Normal; return true;
            case "low": priority = VisitPriority.Low; return true;
            default: return false;
        }
    }
}
