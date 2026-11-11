namespace LifeDrop.Mobile.Visits;

// What the app checks before it sends feedback (plan step 5.30); the server checks the same again.
public static class FeedbackRules
{
    public static readonly IReadOnlyList<string> Kinds = ["suggestion", "complaint", "note"];
    public const int MaxBody = 2000;
    public const int MaxPhotos = 3;
    public const int MaxPhotoBytes = 3 * 1024 * 1024;

    public static bool IsValid(string? kind, string? body) =>
        kind is not null && Kinds.Contains(kind) && !string.IsNullOrWhiteSpace(body) && body.Trim().Length <= MaxBody;

    // The content type the server accepts for a picture file, from its name; null for anything else (the server takes JPEG, PNG and WEBP).
    public static string? PhotoContentType(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null
        };
    }

    // The text key (app.feedback.status.*) of a status; an unknown one reads as new.
    public static string StatusKey(string? status) => status switch
    {
        "in_review" => "app.feedback.status.in_review",
        "done" => "app.feedback.status.done",
        _ => "app.feedback.status.new"
    };

    public static string KindKey(string? kind) => Kinds.Contains(kind ?? "") ? "app.feedback.kind." + kind : "app.feedback.kind.note";
}
