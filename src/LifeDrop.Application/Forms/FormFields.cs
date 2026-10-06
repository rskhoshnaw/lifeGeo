using System.Globalization;
using System.Text.RegularExpressions;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;

namespace LifeDrop.Application.Forms;

// Custom visit-form fields (plan step 3.2, part 2). A company manager (forms.manage) defines extra fields for the visit report details; the assigned
// user answers while the visit is InVisit. Every answer is a string; the server checks it against the field type.
public sealed record FormFieldDto(Guid Id, string Key, string Type, string AppliesTo, string? LabelEn, string? LabelCkb, string? LabelAr, string? LabelFa,
    IReadOnlyList<string> Options, bool IsRequired, int SortOrder, bool IsActive);
public sealed record CreateFormFieldRequest(string? Key, string? Type, string? AppliesTo, string? LabelEn, string? LabelCkb, string? LabelAr, string? LabelFa,
    IReadOnlyList<string>? Options, bool? IsRequired, int? SortOrder);
// Key, type and visit type never change. Options only for a choice field.
public sealed record UpdateFormFieldRequest(string? LabelEn, string? LabelCkb, string? LabelAr, string? LabelFa, IReadOnlyList<string>? Options, bool? IsRequired, int? SortOrder, bool? IsActive);
public sealed record CustomAnswer(Guid FieldId, string? Value);
public sealed record SaveCustomAnswersRequest(IReadOnlyList<CustomAnswer>? Answers);
public sealed record CustomAnswerDto(Guid FieldId, string Key, string? Value);
public sealed record CustomAnswersDto(Guid VisitId, IReadOnlyList<CustomAnswerDto> Answers, DateTimeOffset UpdatedAt);

public sealed record FormFieldData(string? LabelEn, string? LabelCkb, string? LabelAr, string? LabelFa, IReadOnlyList<string> Options, bool IsRequired, int SortOrder, bool IsActive);

public interface IFormFieldStore
{
    // Newest definitions first by sort order then key. appliesTo: only fields for that visit type or all.
    Task<IReadOnlyList<FormFieldDto>> ListAsync(TenantId tenantId, bool includeInactive, string? visitType, CancellationToken cancellationToken);
    Task<int> CountActiveAsync(TenantId tenantId, CancellationToken cancellationToken);
    // Null when the key is taken.
    Task<Guid?> CreateAsync(TenantId tenantId, UserId actor, string key, string type, string appliesTo, FormFieldData data, CancellationToken cancellationToken);
    Task<FormFieldDto?> GetAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken);
    Task<bool> UpdateAsync(TenantId tenantId, UserId actor, Guid id, FormFieldData data, CancellationToken cancellationToken);
    // False when the visit is not InVisit and the actor's (nothing written).
    Task<bool> SaveAnswersAsync(TenantId tenantId, UserId actor, VisitId visitId, IReadOnlyDictionary<Guid, string> answers, CancellationToken cancellationToken);
    Task<CustomAnswersDto?> GetAnswersAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken);
}

public enum FormResult { Success, Forbidden, InvalidData, NotFound, Conflict }

public sealed partial class FormFieldService(IFormFieldStore store, IVisitStore visits, IVisitDetailsStore visitDetails, IPermissionStore permissions)
{
    public static readonly string[] Types = ["text", "number", "choice", "yes_no", "date"];
    public static readonly string[] VisitTypes = ["pharmacy", "doctor", "wholesaler", "other"];
    public const int MaxActiveFields = 30;
    public const int MaxTextLength = 1000;
    public const decimal MaxNumber = 999_999_999_999m;

    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")]
    private static partial Regex KeyPattern();

    public async Task<(FormResult Result, IReadOnlyList<FormFieldDto> Items)> ListAsync(TenantId tenantId, UserId actorId, string? visitType, bool includeInactive, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (FormResult.Forbidden, []);
        visitType = string.IsNullOrWhiteSpace(visitType) ? null : visitType.Trim().ToLowerInvariant();
        if (visitType is not null && !VisitTypes.Contains(visitType)) return (FormResult.InvalidData, []);
        // Inactive definitions are for the managers only; everybody else gets the fields they can answer.
        var manager = await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.FormsManage, cancellationToken);
        return (FormResult.Success, await store.ListAsync(tenantId, includeInactive && manager, visitType, cancellationToken));
    }

    public async Task<(FormResult Result, Guid? Id)> CreateAsync(TenantId tenantId, UserId actorId, CreateFormFieldRequest r, CancellationToken cancellationToken)
    {
        if (!await CanManageAsync(tenantId, actorId, cancellationToken)) return (FormResult.Forbidden, null);
        var key = r.Key?.Trim();
        var type = r.Type?.Trim().ToLowerInvariant();
        var appliesTo = r.AppliesTo?.Trim().ToLowerInvariant();
        if (key is null || !KeyPattern().IsMatch(key) || type is null || !Types.Contains(type) || appliesTo is null || (appliesTo != "all" && !VisitTypes.Contains(appliesTo)) ||
            r.SortOrder is < 0 or > 1000 || !TryLabels(r.LabelEn, r.LabelCkb, r.LabelAr, r.LabelFa, out var labels) || !TryOptions(type, r.Options, out var options))
            return (FormResult.InvalidData, null);
        if (await store.CountActiveAsync(tenantId, cancellationToken) >= MaxActiveFields) return (FormResult.Conflict, null);
        var id = await store.CreateAsync(tenantId, actorId, key, type, appliesTo, new FormFieldData(labels.En, labels.Ckb, labels.Ar, labels.Fa, options, r.IsRequired ?? false, r.SortOrder ?? 0, true), cancellationToken);
        return id is null ? (FormResult.Conflict, null) : (FormResult.Success, id);
    }

    public async Task<FormResult> UpdateAsync(TenantId tenantId, UserId actorId, Guid id, UpdateFormFieldRequest r, CancellationToken cancellationToken)
    {
        if (!await CanManageAsync(tenantId, actorId, cancellationToken)) return FormResult.Forbidden;
        var current = id == Guid.Empty ? null : await store.GetAsync(tenantId, id, cancellationToken);
        if (current is null) return FormResult.NotFound;
        // A label that is not given keeps its value, a blank one is removed; at least one label must stay.
        static string? Merge(string? given, string? existing) => given is null ? existing : string.IsNullOrWhiteSpace(given) ? null : given.Trim();
        if (!TryLabels(Merge(r.LabelEn, current.LabelEn), Merge(r.LabelCkb, current.LabelCkb), Merge(r.LabelAr, current.LabelAr), Merge(r.LabelFa, current.LabelFa), out var labels)) return FormResult.InvalidData;
        var options = current.Options;
        if (r.Options is not null && (current.Type != "choice" || !TryOptions("choice", r.Options, out options))) return FormResult.InvalidData;
        if (r.SortOrder is < 0 or > 1000) return FormResult.InvalidData;
        var active = r.IsActive ?? current.IsActive;
        if (active && !current.IsActive && await store.CountActiveAsync(tenantId, cancellationToken) >= MaxActiveFields) return FormResult.Conflict;
        await store.UpdateAsync(tenantId, actorId, id, new FormFieldData(labels.En, labels.Ckb, labels.Ar, labels.Fa, options, r.IsRequired ?? current.IsRequired, r.SortOrder ?? current.SortOrder, active), cancellationToken);
        return FormResult.Success;
    }

    // Only the assigned user, while the visit is InVisit. The answers replace the earlier set: a blank value removes an answer, every required field must be answered.
    public async Task<FormResult> SaveAnswersAsync(TenantId tenantId, UserId actorId, Guid visitId, SaveCustomAnswersRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return FormResult.Forbidden;
        var visit = visitId == Guid.Empty ? null : await visits.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return FormResult.NotFound;
        if (visit.UserId != actorId) return FormResult.Forbidden;
        if (visit.Status != VisitStatus.InVisit) return FormResult.Conflict;
        var type = await visitDetails.GetVisitTypeAsync(tenantId, visit.Id, cancellationToken);
        if (type is null || request.Answers is null) return FormResult.InvalidData;
        var fields = (await store.ListAsync(tenantId, false, type.Value.ToString().ToLowerInvariant(), cancellationToken)).ToDictionary(f => f.Id);
        var clean = new Dictionary<Guid, string>();
        foreach (var answer in request.Answers)
        {
            if (answer.FieldId == Guid.Empty || !fields.TryGetValue(answer.FieldId, out var field)) return FormResult.InvalidData;
            var value = answer.Value?.Trim();
            if (string.IsNullOrEmpty(value)) { clean[answer.FieldId] = ""; continue; }
            var normal = Normalise(field, value);
            if (normal is null) return FormResult.InvalidData;
            clean[answer.FieldId] = normal;
        }
        if (request.Answers.Select(a => a.FieldId).Distinct().Count() != request.Answers.Count) return FormResult.InvalidData;
        foreach (var field in fields.Values.Where(f => f.IsRequired))
            if (!clean.TryGetValue(field.Id, out var given) || given.Length == 0) return FormResult.InvalidData;
        var kept = clean.Where(p => p.Value.Length > 0).ToDictionary(p => p.Key, p => p.Value);
        return await store.SaveAnswersAsync(tenantId, actorId, visit.Id, kept, cancellationToken) ? FormResult.Success : FormResult.Conflict;
    }

    public async Task<(FormResult Result, CustomAnswersDto? Answers)> GetAnswersAsync(TenantId tenantId, UserId actorId, Guid visitId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (FormResult.Forbidden, null);
        var visit = visitId == Guid.Empty ? null : await visits.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null || !await VisitReports.CanReadAsync(permissions, tenantId, actorId, visit, cancellationToken)) return (FormResult.NotFound, null);
        var answers = await store.GetAnswersAsync(tenantId, visit.Id, cancellationToken);
        return answers is null ? (FormResult.NotFound, null) : (FormResult.Success, answers);
    }

    // The stored form of a valid answer, or null.
    public static string? Normalise(FormFieldDto field, string value)
    {
        switch (field.Type)
        {
            case "text": return value.Length <= MaxTextLength ? value : null;
            case "number":
                if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) || Math.Abs(n) > MaxNumber || decimal.Round(n, 3) != n) return null;
                return n.ToString("0.###", CultureInfo.InvariantCulture);
            case "choice": return field.Options.Contains(value) ? value : null;
            case "yes_no": return value is "true" or "false" ? value : null;
            case "date": return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            default: return null;
        }
    }

    private static bool TryLabels(string? en, string? ckb, string? ar, string? fa, out (string? En, string? Ckb, string? Ar, string? Fa) labels)
    {
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        labels = (Clean(en), Clean(ckb), Clean(ar), Clean(fa));
        var all = new[] { labels.En, labels.Ckb, labels.Ar, labels.Fa };
        return all.Any(l => l is not null) && all.All(l => l is null || l.Length <= 100);
    }

    private static bool TryOptions(string type, IReadOnlyList<string>? given, out IReadOnlyList<string> options)
    {
        var list = (given ?? []).Select(o => o?.Trim() ?? "").ToList();
        options = list;
        if (type != "choice") return list.Count == 0;
        return list.Count is >= 2 and <= 20 && list.All(o => o.Length is >= 1 and <= 100) && list.Distinct().Count() == list.Count;
    }

    private async Task<bool> CanManageAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        !tenantId.IsEmpty && !actorId.IsEmpty && await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.FormsManage, cancellationToken);
}
