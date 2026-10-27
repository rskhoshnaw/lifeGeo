using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Customers;

// Import of a doctor list (plan step 5.9, columns in docs/doctors-import.md). The panel reads the CSV and sends the rows; every row is checked first and nothing is created when any row is wrong
// (DryRun checks only). Doctors that already exist (same name and phone) are skipped, so the same file can be sent again. Places (city, neighbourhood, street) are matched by name, new ones are
// added. VisitorEmail makes an existing user the owner and takes the region of the user's team; without it the caller owns the doctor. Only a caller with customers.write for the whole company
// (the sales manager) may import, and never a role that cannot see doctors.
public sealed record DoctorImportRow(string? Name, string? ClinicName = null, string? Specialty = null, bool? Cooperates = null, bool? Dealer = null, string? CustomerClass = null,
    string? Phone = null, string? Phone2 = null, string? City = null, string? Neighbourhood = null, string? Street = null, string? Address = null, double? Latitude = null,
    double? Longitude = null, string? Notes = null, string? VisitorEmail = null);

public sealed record ImportDoctorsRequest(IReadOnlyList<DoctorImportRow>? Rows, bool? DryRun = null);

// Row: the number of the row in the file (1 = the first row after the header). Code: required | too_long | invalid | not_found.
public sealed record DoctorImportError(int Row, string Field, string Code);

public sealed record ImportDoctorsResult(int Created, int Duplicates, IReadOnlyList<DoctorImportError> Errors, bool DryRun);

// A checked row: texts trimmed (empty = null), email in lower case, the owner and region of the visitor.
public sealed record DoctorToImport(int Row, DoctorImportRow Data, Guid? OwnerId, Guid? RegionId);

public enum DoctorImportOutcome { Success, Forbidden, InvalidData }

public interface IDoctorImportStore
{
    // Active users of the company by lower-case e-mail, with the region of their team.
    Task<IReadOnlyDictionary<string, (Guid UserId, Guid? RegionId)>> FindUsersAsync(TenantId tenantId, IReadOnlyCollection<string> emails, CancellationToken cancellationToken);
    // Creates the doctors in one transaction (rolled back for a dry run) and returns how many were created and how many already existed.
    Task<(int Created, int Duplicates)> ImportAsync(TenantId tenantId, UserId actorId, IReadOnlyList<DoctorToImport> rows, bool dryRun, CancellationToken cancellationToken);
}

public sealed class DoctorImport(IDoctorImportStore store, IPermissionStore permissions)
{
    public const int MaxRows = 500;

    public async Task<(DoctorImportOutcome Outcome, ImportDoctorsResult? Result)> ImportAsync(TenantId tenantId, UserId actorId, ImportDoctorsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersWrite, cancellationToken);
        if (scope is null || scope.Scope != DataScope.Tenant || scope.HideDoctors) return (DoctorImportOutcome.Forbidden, null);
        if (request.Rows is null || request.Rows.Count == 0 || request.Rows.Count > MaxRows || request.Rows.Any(r => r is null)) return (DoctorImportOutcome.InvalidData, null);

        var errors = new List<DoctorImportError>();
        var cleaned = new List<DoctorImportRow>(request.Rows.Count);
        for (var i = 0; i < request.Rows.Count; i++) cleaned.Add(Clean(request.Rows[i], i + 1, errors));

        var emails = cleaned.Select(r => r.VisitorEmail).Where(e => e is not null).Select(e => e!).Distinct().ToList();
        IReadOnlyDictionary<string, (Guid UserId, Guid? RegionId)> users = emails.Count == 0
            ? new Dictionary<string, (Guid UserId, Guid? RegionId)>()
            : await store.FindUsersAsync(tenantId, emails, cancellationToken);
        var rows = new List<DoctorToImport>(cleaned.Count);
        for (var i = 0; i < cleaned.Count; i++)
        {
            Guid? owner = null, region = null;
            if (cleaned[i].VisitorEmail is { } email)
            {
                if (users.TryGetValue(email, out var user)) (owner, region) = (user.UserId, user.RegionId);
                else errors.Add(new DoctorImportError(i + 1, "visitor_email", "not_found"));
            }
            rows.Add(new DoctorToImport(i + 1, cleaned[i], owner, region));
        }

        var dryRun = request.DryRun == true;
        if (errors.Count > 0) return (DoctorImportOutcome.Success, new ImportDoctorsResult(0, 0, errors, dryRun));
        var (created, duplicates) = await store.ImportAsync(tenantId, actorId, rows, dryRun, cancellationToken);
        return (DoctorImportOutcome.Success, new ImportDoctorsResult(created, duplicates, Array.Empty<DoctorImportError>(), dryRun));
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Trims the texts and records what is wrong with the row.
    public static DoctorImportRow Clean(DoctorImportRow row, int number, List<DoctorImportError> errors)
    {
        string? Limited(string? value, string field, int max, bool required = false)
        {
            var text = Text(value);
            if (text is null && required) errors.Add(new DoctorImportError(number, field, "required"));
            else if (text?.Length > max) errors.Add(new DoctorImportError(number, field, "too_long"));
            return text;
        }

        var name = Limited(row.Name, "name", 150, required: true);
        var clinic = Limited(row.ClinicName, "clinic_name", 150);
        var specialty = Text(row.Specialty)?.ToLowerInvariant();
        if (specialty is not null && !DoctorSpecialties.Keys.Contains(specialty)) errors.Add(new DoctorImportError(number, "specialty", "invalid"));
        var customerClass = Text(row.CustomerClass)?.ToUpperInvariant();
        if (customerClass is not null && !DoctorSpecialties.Classes.Contains(customerClass)) errors.Add(new DoctorImportError(number, "class", "invalid"));
        var phone = Limited(row.Phone, "phone", 40);
        var phone2 = Limited(row.Phone2, "phone2", 40);
        var city = Limited(row.City, "city", 100);
        var neighbourhood = Limited(row.Neighbourhood, "neighbourhood", 100);
        var street = Limited(row.Street, "street", 100);
        if (city is null && (neighbourhood is not null || street is not null)) errors.Add(new DoctorImportError(number, "city", "required"));
        if (neighbourhood is null && street is not null) errors.Add(new DoctorImportError(number, "neighbourhood", "required"));
        var address = Limited(row.Address, "address", 300);
        var notes = Limited(row.Notes, "notes", 500);
        var email = Limited(row.VisitorEmail, "visitor_email", 200)?.ToLowerInvariant();
        if (email is not null && !email.Contains('@')) errors.Add(new DoctorImportError(number, "visitor_email", "invalid"));

        if ((row.Latitude is null) != (row.Longitude is null)) errors.Add(new DoctorImportError(number, "latitude", "invalid"));
        else if (row.Latitude is { } lat && row.Longitude is { } lon && (double.IsNaN(lat) || double.IsNaN(lon) || lat is < -90 or > 90 || lon is < -180 or > 180))
            errors.Add(new DoctorImportError(number, "latitude", "invalid"));

        return new DoctorImportRow(name, clinic, specialty, row.Cooperates, row.Dealer, customerClass, phone, phone2, city, neighbourhood, street, address, row.Latitude, row.Longitude, notes, email);
    }
}
