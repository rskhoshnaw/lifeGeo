using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;

namespace LifeDrop.Application.Visits;

// Specialised visit forms (plan step 2.7). Beside the base report: purpose, person met and role, products presented, needs, and the fields of the
// visit's type only (doctor: specialty, daily patients; pharmacy: monthly sales, competitors; wholesaler: monthly volume, payment terms).
// Purpose: product_intro | order_follow_up | collection | complaint | other. Currency (IQD | USD) goes with monthly sales or volume.
public sealed record SaveVisitDetailsRequest(string? Purpose, string? PersonMet, string? PersonRole, string? Needs, IReadOnlyList<Guid>? ProductIds, string? Specialty, int? DailyPatients,
    decimal? MonthlySales, string? Competitors, decimal? MonthlyVolume, string? PaymentTerms, string? Currency);

public sealed record DetailsProductDto(Guid Id, string Code, string Name);
public sealed record VisitDetailsDto(Guid VisitId, string VisitType, string Purpose, string? PersonMet, string? PersonRole, string? Needs, IReadOnlyList<DetailsProductDto> Products,
    string? Specialty, int? DailyPatients, decimal? MonthlySales, string? Competitors, decimal? MonthlyVolume, string? PaymentTerms, string? Currency, DateTimeOffset SubmittedAt,
    DateTimeOffset UpdatedAt);

public interface IVisitDetailsStore
{
    // InvalidData when a product id is not an active catalogue product; Conflict when the visit is no longer InVisit and the caller's (nothing written).
    Task<ManageVisitResult> SaveAsync(TenantId tenantId, UserId actorId, VisitId visitId, SaveVisitDetailsRequest details, CancellationToken cancellationToken);
    Task<VisitDetailsDto?> GetAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken);
    // pharmacy | doctor | wholesaler | other (the visit's type; the visit loader does not carry it).
    Task<VisitType?> GetVisitTypeAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken);
}

public sealed class VisitReportDetails(IVisitStore visits, IVisitDetailsStore store, LifeDrop.Application.Roles.IPermissionStore permissions)
{
    public static readonly string[] Purposes = ["product_intro", "order_follow_up", "collection", "complaint", "other"];
    public const decimal MaxAmount = 999_999_999_999m;

    public async Task<ManageVisitResult> SaveAsync(TenantId tenantId, UserId actorId, Guid visitId, SaveVisitDetailsRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return ManageVisitResult.Forbidden;
        var visit = visitId == Guid.Empty ? null : await visits.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return ManageVisitResult.NotFound;
        if (visit.UserId != actorId) return ManageVisitResult.Forbidden;
        if (visit.Status != VisitStatus.InVisit) return ManageVisitResult.InvalidState;
        var type = await store.GetVisitTypeAsync(tenantId, visit.Id, cancellationToken);
        var clean = type is null ? null : Clean(request, type.Value);
        return clean is null ? ManageVisitResult.InvalidData : await store.SaveAsync(tenantId, actorId, visit.Id, clean, cancellationToken);
    }

    public async Task<(ManageVisitResult Result, VisitDetailsDto? Details)> GetAsync(TenantId tenantId, UserId actorId, Guid visitId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (ManageVisitResult.Forbidden, null);
        var visit = visitId == Guid.Empty ? null : await visits.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null || !await VisitReports.CanReadAsync(permissions, tenantId, actorId, visit, cancellationToken)) return (ManageVisitResult.NotFound, null);
        var details = await store.GetAsync(tenantId, visit.Id, cancellationToken);
        return details is null ? (ManageVisitResult.NotFound, null) : (ManageVisitResult.Success, details);
    }

    private static string? Text(string? value, int max, out bool tooLong)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        tooLong = trimmed?.Length > max;
        return trimmed;
    }

    // Null when something is wrong. Fields of another visit type are refused, not ignored, so a form never silently drops what a rep typed.
    private static SaveVisitDetailsRequest? Clean(SaveVisitDetailsRequest r, VisitType type)
    {
        if (r.Purpose is null || !Purposes.Contains(r.Purpose)) return null;
        var person = Text(r.PersonMet, 100, out var l1);
        var role = Text(r.PersonRole, 100, out var l2);
        var needs = Text(r.Needs, 1000, out var l3);
        var specialty = Text(r.Specialty, 100, out var l4);
        var competitors = Text(r.Competitors, 500, out var l5);
        var terms = Text(r.PaymentTerms, 500, out var l6);
        if (l1 || l2 || l3 || l4 || l5 || l6) return null;
        var products = (r.ProductIds ?? Array.Empty<Guid>()).ToList();
        if (products.Count > 50 || products.Any(p => p == Guid.Empty) || products.Distinct().Count() != products.Count) return null;

        var doctorFields = specialty is not null || r.DailyPatients is not null;
        var pharmacyFields = r.MonthlySales is not null || competitors is not null;
        var wholesalerFields = r.MonthlyVolume is not null || terms is not null;
        if ((doctorFields && type != VisitType.Doctor) || (pharmacyFields && type != VisitType.Pharmacy) || (wholesalerFields && type != VisitType.Wholesaler)) return null;
        if (r.DailyPatients is < 0 or > 10_000) return null;
        foreach (var amount in new[] { r.MonthlySales, r.MonthlyVolume })
            if (amount is < 0 or > MaxAmount || (amount is { } a && decimal.Round(a, 3) != a)) return null;
        var hasAmount = r.MonthlySales is not null || r.MonthlyVolume is not null;
        if (hasAmount != (r.Currency is not null) || (r.Currency is not null && r.Currency is not ("IQD" or "USD"))) return null;

        return r with { PersonMet = person, PersonRole = role, Needs = needs, Specialty = specialty, Competitors = competitors, PaymentTerms = terms, ProductIds = products };
    }
}
