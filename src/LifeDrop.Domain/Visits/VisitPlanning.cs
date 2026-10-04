namespace LifeDrop.Domain.Visits;

// Visit type selects the specialised report form later (PRD §5.3).
// Contract values are stored as text: pharmacy, doctor, wholesaler, other.
public enum VisitType { Pharmacy, Doctor, Wholesaler, Other }

// Contract values are stored as text: high, normal, low.
public enum VisitPriority { High, Normal, Low }

public static class VisitPlanningContract
{
    public static string ToContract(this VisitType type) => type switch
    {
        VisitType.Pharmacy => "pharmacy",
        VisitType.Doctor => "doctor",
        VisitType.Wholesaler => "wholesaler",
        _ => "other"
    };

    public static string ToContract(this VisitPriority priority) => priority switch
    {
        VisitPriority.High => "high",
        VisitPriority.Low => "low",
        _ => "normal"
    };

    // Null means "not given" and maps to the default (other / normal).
    public static bool TryParseType(string? value, out VisitType type)
    {
        type = VisitType.Other;
        switch (value)
        {
            case null: return true;
            case "pharmacy": type = VisitType.Pharmacy; return true;
            case "doctor": type = VisitType.Doctor; return true;
            case "wholesaler": type = VisitType.Wholesaler; return true;
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
