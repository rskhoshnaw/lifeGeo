using System.Collections.Generic;
using System.Linq;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Customers;

public enum CreateCustomerResult { Created, DuplicateCode, Forbidden, InvalidData, RegionNotFound }

// RegionId: optional; defaults to the creator's region (plan step 1.15).
public sealed record CreateCustomerRequest(string Type, string Code, string Name, Guid? RegionId = null);

public interface ICustomerStore
{
    // scope limits rows to the caller's data scope (plan step 0.6b); out-of-scope rows behave as not found.
    Task<CustomerDto?> GetByIdAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken);
    Task<CreateCustomerResult> CreateAsync(TenantId tenantId, UserId actorId, CustomerId id, CreateCustomerRequest request, CancellationToken cancellationToken);
    // With region and approval status (plan step 1.15); RegionNotFound when the region is not in the tenant.
    Task<CreateCustomerResult> CreateAsync(TenantId tenantId, UserId actorId, CustomerId id, CreateCustomerRequest request, Guid? regionId,
        string approvalStatus, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    Task<IReadOnlyList<CustomerDto>> ListAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken);
    // Archived customers (plan step 4.11) are left out of ListAsync (and so out of the map); this lists only them.
    Task<IReadOnlyList<CustomerDto>> ListArchivedAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // Type, code and name (plan step 4.11); NotFound when the customer is not in the actor's scope, Conflict when the code belongs to another customer.
    // One audit row (customer.edited) in the same transaction.
    Task<CustomerUpdateResult> EditAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, string type, string code, string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    // Contact person, second phone and note (plan step 4.15); null clears. false when the customer is not in the actor's scope. Audit customer.contact_changed.
    Task<bool> SetContactAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, string? contactName, string? phone2, string? notes, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    // Doctor details (plan step 4.27); null clears. false when the customer is not in the actor's scope. Audit customer.doctor_changed.
    Task<bool> SetDoctorAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, CustomerDoctorRequest details, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    // Active customers whose name, code or phone matches the text (plan step 4.15).
    Task<IReadOnlyList<CustomerDto>> SearchAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, string search, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    // Archive or restore (audit customer.archived / customer.restored); false when the customer is not in the actor's scope.
    Task<bool> SetArchivedAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, bool archived, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // Location and geofence updates (plan step 1.5); false when the customer is not in the actor's scope.
    // Each writes its audit row in the same transaction.
    Task<bool> SetLocationAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope,
        double latitude, double longitude, string source, double? accuracyMeters, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    Task<bool> SetGeofenceRadiusAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope,
        int? radiusMeters, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // Customer profile (plan step 1.23-b): contact details and the place in the reference list. Replaces the five
    // fields (null clears). NotFound when the customer is not in the actor's scope; InvalidData when the place is missing,
    // inactive or merged away. One audit row in the same transaction.
    Task<CustomerProfileResult> SetProfileAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope,
        CustomerProfileRequest profile, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // Accepted visit days and hours (plan step 1.23). Get: null when the customer is not in the actor's scope.
    // Replace: swaps the whole list in one transaction with its audit row; false when not in scope.
    Task<IReadOnlyList<VisitWindowDto>?> GetVisitWindowsAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    Task<bool> ReplaceVisitWindowsAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope,
        IReadOnlyList<VisitWindowDto> windows, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

// Customer registration (plan step 1.15, PRD §8, decision D11). The customer is placed in the requested region,
// or the creator's region. A user without tenant-wide scope who registers a customer in another region creates it
// as 'pending'; it waits for a sales manager or that region's supervisor and stays owned by the creator.
public sealed class CreateCustomer(ICustomerStore store, IPermissionStore permissions)
{
    public async Task<(CreateCustomerResult Result, CustomerId Id)> ExecuteAsync(TenantId tenantId, UserId actorId, CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (CreateCustomerResult.Forbidden, CustomerId.Empty);
        if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name) ||
            request.RegionId == Guid.Empty)
            return (CreateCustomerResult.InvalidData, CustomerId.Empty);

        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersWrite, cancellationToken);
        if (scope is null) return (CreateCustomerResult.Forbidden, CustomerId.Empty);
        var (_, creatorRegion) = await permissions.GetUserRegionAsync(tenantId, actorId, cancellationToken);
        var region = request.RegionId ?? creatorRegion;
        var pending = scope.Scope != DataScope.Tenant && region is not null && region != creatorRegion;

        var id = CustomerId.New();
        var result = await store.CreateAsync(tenantId, actorId, id, request, region, pending ? "pending" : "approved", cancellationToken);
        return (result, id);
    }
}

public sealed record DecideCustomerApprovalRequest(string? Decision, string? Note);
public enum CustomerApprovalResult { Success, Forbidden, NotFound, InvalidData, AlreadyDecided }

public interface ICustomerApprovalStore
{
    // Pending customers, optionally only in one region.
    Task<IReadOnlyList<CustomerDto>> ListPendingAsync(TenantId tenantId, Guid? regionId, int limit, CancellationToken cancellationToken);
    Task<(Guid? RegionId, string Status)?> GetApprovalAsync(TenantId tenantId, CustomerId id, CancellationToken cancellationToken);
    // Decides only while still pending; audited in the same transaction.
    Task<bool> TryDecideAsync(TenantId tenantId, UserId actorId, CustomerId id, string decision, string? note, CancellationToken cancellationToken);
}

// Approval of customers registered outside the rep's region (plan step 1.15, D11): customers.approve with tenant
// scope (sales manager) for every region, with region scope (supervisor) only for the supervisor's region.
public sealed class CustomerApprovals(ICustomerApprovalStore store, IPermissionStore permissions)
{
    public const int MaxNoteLength = 500;

    public async Task<(CustomerApprovalResult Result, IReadOnlyList<CustomerDto>? Items)> ListAsync(TenantId tenantId, UserId actorId,
        CancellationToken cancellationToken)
    {
        var scope = await ApproverScopeAsync(tenantId, actorId, cancellationToken);
        if (scope is null) return (CustomerApprovalResult.Forbidden, null);
        if (scope.Scope == DataScope.Region && scope.RegionId is null) return (CustomerApprovalResult.Success, Array.Empty<CustomerDto>());
        return (CustomerApprovalResult.Success,
            await store.ListPendingAsync(tenantId, scope.Scope == DataScope.Tenant ? null : scope.RegionId, 200, cancellationToken));
    }

    public async Task<CustomerApprovalResult> DecideAsync(TenantId tenantId, UserId actorId, Guid customerId, DecideCustomerApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var scope = await ApproverScopeAsync(tenantId, actorId, cancellationToken);
        if (scope is null) return CustomerApprovalResult.Forbidden;
        var decision = request.Decision switch { "approve" => "approved", "reject" => "rejected", _ => null };
        var note = request.Note?.Trim();
        if (decision is null || note is { Length: > MaxNoteLength } || (decision == "rejected" && string.IsNullOrEmpty(note)))
            return CustomerApprovalResult.InvalidData;

        var found = customerId == Guid.Empty ? null : await store.GetApprovalAsync(tenantId, new CustomerId(customerId), cancellationToken);
        if (found is not { } customer ||
            (scope.Scope == DataScope.Region && (scope.RegionId is null || customer.RegionId != scope.RegionId)))
            return CustomerApprovalResult.NotFound;
        if (customer.Status != "pending") return CustomerApprovalResult.AlreadyDecided;
        return await store.TryDecideAsync(tenantId, actorId, new CustomerId(customerId), decision, string.IsNullOrEmpty(note) ? null : note, cancellationToken)
            ? CustomerApprovalResult.Success : CustomerApprovalResult.AlreadyDecided;
    }

    private async Task<AccessScope?> ApproverScopeAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return null;
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersApprove, cancellationToken);
        return scope is { Scope: DataScope.Tenant or DataScope.Region } ? scope : null;
    }
}

public sealed class GetCustomer(ICustomerStore store)
{
    public Task<CustomerDto?> ExecuteAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || id.IsEmpty) return Task.FromResult<CustomerDto?>(null);
        return store.GetByIdAsync(tenantId, id, scope, cancellationToken);
    }
}

public sealed class ListCustomers(ICustomerStore store)
{
    public Task<(bool IsValid, IReadOnlyList<CustomerDto> Items)> ExecuteAsync(TenantId tenantId, AccessScope scope, int? limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken) =>
        ExecuteAsync(tenantId, scope, limit, cursorCreatedAt, cursorId, archived: false, cancellationToken);

    public const int MaxSearchLength = 100;

    // With a search text (plan step 4.15, at least two characters) the list holds only matching active customers.
    public async Task<(bool IsValid, IReadOnlyList<CustomerDto> Items)> SearchAsync(TenantId tenantId, AccessScope scope, int? limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, string? search, CancellationToken cancellationToken)
    {
        var text = search?.Trim();
        if (string.IsNullOrEmpty(text)) return await ExecuteAsync(tenantId, scope, limit, cursorCreatedAt, cursorId, cancellationToken);
        var actualLimit = limit ?? 20;
        if (tenantId.IsEmpty || text.Length < 2 || text.Length > MaxSearchLength || actualLimit is <= 0 or > 100 || cursorCreatedAt.HasValue != cursorId.HasValue)
            return (false, Array.Empty<CustomerDto>());
        return (true, await store.SearchAsync(tenantId, scope, actualLimit, cursorCreatedAt, cursorId, text, cancellationToken));
    }

    public async Task<(bool IsValid, IReadOnlyList<CustomerDto> Items)> ExecuteAsync(TenantId tenantId, AccessScope scope, int? limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, bool archived, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty) return (false, Array.Empty<CustomerDto>());
        
        var actualLimit = limit ?? 20;
        if (actualLimit <= 0 || actualLimit > 100) return (false, Array.Empty<CustomerDto>());
        if (cursorCreatedAt.HasValue != cursorId.HasValue) return (false, Array.Empty<CustomerDto>());

        var items = archived
            ? await store.ListArchivedAsync(tenantId, scope, actualLimit, cursorCreatedAt, cursorId, cancellationToken)
            : await store.ListAsync(tenantId, scope, actualLimit, cursorCreatedAt, cursorId, cancellationToken);
        return (true, items);
    }
}

// Source "gps": captured on site, AccuracyMeters (0 < a <= 100) required. Source "manual": placed by a person on a map.
public sealed record SetCustomerLocationRequest(double Latitude, double Longitude, string Source, double? AccuracyMeters = null);
// RadiusMeters 5-1000; null restores the default of 20 m.
public sealed record SetGeofenceRadiusRequest(int? RadiusMeters);

public enum CustomerUpdateResult { Success, Forbidden, NotFound, InvalidData, Conflict }

// Customer location and geofence radius (plan step 1.5, PRD §6.1, §8). The customer must be within the actor's
// data scope for the permission; changes are audited.
public sealed class CustomerLocationService(ICustomerStore store, IPermissionStore permissions)
{
    public const double MaxGpsAccuracyMeters = 100;
    public const int MinRadiusMeters = 5;
    public const int MaxRadiusMeters = 1000;

    public async Task<CustomerUpdateResult> SetLocationAsync(TenantId tenantId, UserId actorId, Guid customerId, SetCustomerLocationRequest request, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.CustomersLocationWrite, cancellationToken);
        if (scope is null) return CustomerUpdateResult.Forbidden;
        if (!IsValidLocation(request)) return CustomerUpdateResult.InvalidData;

        return await store.SetLocationAsync(tenantId, actorId, new CustomerId(customerId), scope, request.Latitude, request.Longitude,
            request.Source, request.Source == "gps" ? request.AccuracyMeters : null, cancellationToken)
            ? CustomerUpdateResult.Success : CustomerUpdateResult.NotFound;
    }

    public async Task<CustomerUpdateResult> SetGeofenceRadiusAsync(TenantId tenantId, UserId actorId, Guid customerId, SetGeofenceRadiusRequest request, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.CustomersGeofenceWrite, cancellationToken);
        if (scope is null) return CustomerUpdateResult.Forbidden;
        if (request.RadiusMeters is < MinRadiusMeters or > MaxRadiusMeters) return CustomerUpdateResult.InvalidData;

        return await store.SetGeofenceRadiusAsync(tenantId, actorId, new CustomerId(customerId), scope, request.RadiusMeters, cancellationToken)
            ? CustomerUpdateResult.Success : CustomerUpdateResult.NotFound;
    }

    // Rejects out-of-range values, the 0,0 "null island" default of broken GPS, and imprecise GPS fixes.
    public static bool IsValidLocation(SetCustomerLocationRequest request) =>
        double.IsFinite(request.Latitude) && double.IsFinite(request.Longitude) &&
        request.Latitude is >= -90 and <= 90 && request.Longitude is >= -180 and <= 180 &&
        !(request.Latitude == 0 && request.Longitude == 0) &&
        request.Source switch
        {
            "gps" => request.AccuracyMeters is > 0 and <= MaxGpsAccuracyMeters,
            "manual" => true,
            _ => false
        };

    private async Task<AccessScope?> ScopeAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, permission, cancellationToken);
}

// Weekday: 0 = Sunday .. 6 = Saturday. Times are local (Asia/Baghdad); OpensAt must be before ClosesAt.
public sealed record VisitWindowDto(int Weekday, TimeOnly OpensAt, TimeOnly ClosesAt);

public sealed record SetVisitWindowsRequest(IReadOnlyList<VisitWindowDto> Windows);

// Days and hours in which a customer accepts visits (plan step 1.23). A customer without windows has no restriction.
// Reading needs customers.read, changing customers.write, both within the actor's data scope; changes are audited.
public sealed class CustomerVisitWindows(ICustomerStore store, IPermissionStore permissions)
{
    public const int MaxWindows = 21;

    public async Task<(CustomerUpdateResult Result, IReadOnlyList<VisitWindowDto>? Windows)> GetAsync(TenantId tenantId, UserId actorId, Guid customerId,
        CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken);
        if (scope is null) return (CustomerUpdateResult.Forbidden, null);
        var windows = await store.GetVisitWindowsAsync(tenantId, new CustomerId(customerId), scope, cancellationToken);
        return windows is null ? (CustomerUpdateResult.NotFound, null) : (CustomerUpdateResult.Success, windows);
    }

    public async Task<CustomerUpdateResult> SetAsync(TenantId tenantId, UserId actorId, Guid customerId, SetVisitWindowsRequest request,
        CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.CustomersWrite, cancellationToken);
        if (scope is null) return CustomerUpdateResult.Forbidden;
        if (!IsValid(request.Windows)) return CustomerUpdateResult.InvalidData;

        var ordered = request.Windows.OrderBy(w => w.Weekday).ThenBy(w => w.OpensAt).ToList();
        return await store.ReplaceVisitWindowsAsync(tenantId, actorId, new CustomerId(customerId), scope, ordered, cancellationToken)
            ? CustomerUpdateResult.Success : CustomerUpdateResult.NotFound;
    }

    // At most 21 windows; weekday 0-6; opens before closes; windows of one day must not overlap.
    public static bool IsValid(IReadOnlyList<VisitWindowDto>? windows)
    {
        if (windows is null || windows.Count > MaxWindows) return false;
        if (windows.Any(w => w is null || w.Weekday is < 0 or > 6 || w.OpensAt >= w.ClosesAt)) return false;
        foreach (var day in windows.GroupBy(w => w.Weekday))
        {
            var sorted = day.OrderBy(w => w.OpensAt).ToList();
            for (var i = 1; i < sorted.Count; i++)
                if (sorted[i].OpensAt < sorted[i - 1].ClosesAt) return false;
        }
        return true;
    }

    private async Task<AccessScope?> ScopeAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, permission, cancellationToken);
}

// PlaceId: the most precise place known (a city, neighbourhood, street or alley), or null.
public sealed record CustomerProfileRequest(string? Phone, string? Address, Guid? PlaceId);

public enum CustomerProfileResult { Success, NotFound, InvalidData }

// Phone and address of a customer and where it is (plan step 1.23-b). Needs customers.write within the actor's data scope.
public sealed class CustomerProfileService(ICustomerStore store, IPermissionStore permissions)
{
    public const int MaxPhoneLength = 40;
    public const int MaxAddressLength = 300;

    public async Task<(CustomerUpdateResult Result, CustomerProfileResult Detail)> SetAsync(TenantId tenantId, UserId actorId, Guid customerId,
        CustomerProfileRequest request, CancellationToken cancellationToken)
    {
        var scope = tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersWrite, cancellationToken);
        if (scope is null) return (CustomerUpdateResult.Forbidden, CustomerProfileResult.NotFound);

        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        var address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
        if (phone is { Length: > MaxPhoneLength } || address is { Length: > MaxAddressLength } ||
            request.PlaceId == Guid.Empty)
            return (CustomerUpdateResult.InvalidData, CustomerProfileResult.InvalidData);

        var result = await store.SetProfileAsync(tenantId, actorId, new CustomerId(customerId), scope,
            request with { Phone = phone, Address = address }, cancellationToken);
        return result switch
        {
            CustomerProfileResult.Success => (CustomerUpdateResult.Success, result),
            CustomerProfileResult.NotFound => (CustomerUpdateResult.NotFound, result),
            _ => (CustomerUpdateResult.InvalidData, result)
        };
    }
}

public sealed record EditCustomerRequest(string? Type, string? Code, string? Name);
public sealed record CustomerContactRequest(string? ContactName, string? Phone2, string? Notes);
public sealed record CustomerDoctorRequest(string? ClinicName, string? Specialty, bool? Cooperates, bool? Dealer, string? CustomerClass);

// The fixed list of doctor specialties (plan step 4.27); the texts are `specialty.<key>` in i18n/strings.json.
public static class DoctorSpecialties
{
    public static readonly IReadOnlyList<string> Keys = ["orthopedics", "neuro_spine", "pediatrics", "women_obstetrics", "surgery", "kidney_urology", "ent", "other"];
    public static readonly IReadOnlyList<string> Classes = ["A+", "A", "B", "C"];
}

// Renaming, recoding and archiving customers from the panel (plan step 4.11). Editing needs customers.write, archiving and restoring customers.approve
// (a manager or supervisor), each within the actor's data scope. A customer is never deleted: visits, orders, payments and reports keep referring to it.
public sealed class CustomerEditService(ICustomerStore store, IPermissionStore permissions)
{
    public const int MaxTypeLength = 40;
    public const int MaxCodeLength = 50;
    public const int MaxNameLength = 200;
    public const int MaxContactNameLength = 100;
    public const int MaxPhone2Length = 40;
    public const int MaxNotesLength = 500;
    public const int MaxClinicNameLength = 150;

    public async Task<CustomerUpdateResult> EditAsync(TenantId tenantId, UserId actorId, Guid customerId, EditCustomerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersWrite, cancellationToken);
        if (scope is null) return CustomerUpdateResult.Forbidden;
        var type = request.Type?.Trim();
        var code = request.Code?.Trim();
        var name = request.Name?.Trim();
        if (customerId == Guid.Empty || string.IsNullOrEmpty(type) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name) ||
            type.Length > MaxTypeLength || code.Length > MaxCodeLength || name.Length > MaxNameLength)
            return CustomerUpdateResult.InvalidData;
        return await store.EditAsync(tenantId, actorId, new CustomerId(customerId), scope, type, code, name, cancellationToken);
    }

    // Contact person, second phone and note, by whoever may edit the customer (customers.write within the data scope). Empty text clears a field.
    public async Task<CustomerUpdateResult> SetContactAsync(TenantId tenantId, UserId actorId, Guid customerId, CustomerContactRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersWrite, cancellationToken);
        if (scope is null) return CustomerUpdateResult.Forbidden;
        var contact = Clean(request.ContactName);
        var phone2 = Clean(request.Phone2);
        var notes = Clean(request.Notes);
        if (customerId == Guid.Empty || contact?.Length > MaxContactNameLength || phone2?.Length > MaxPhone2Length || notes?.Length > MaxNotesLength) return CustomerUpdateResult.InvalidData;
        return await store.SetContactAsync(tenantId, actorId, new CustomerId(customerId), scope, contact, phone2, notes, cancellationToken)
            ? CustomerUpdateResult.Success : CustomerUpdateResult.NotFound;
    }

    // Doctor details, by whoever may edit the customer (customers.write within the data scope). Empty text / null clears a field.
    public async Task<CustomerUpdateResult> SetDoctorAsync(TenantId tenantId, UserId actorId, Guid customerId, CustomerDoctorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersWrite, cancellationToken);
        if (scope is null) return CustomerUpdateResult.Forbidden;
        var clinic = Clean(request.ClinicName);
        var specialty = Clean(request.Specialty);
        var customerClass = Clean(request.CustomerClass);
        if (customerId == Guid.Empty || clinic?.Length > MaxClinicNameLength || (specialty is not null && !DoctorSpecialties.Keys.Contains(specialty)) ||
            (customerClass is not null && !DoctorSpecialties.Classes.Contains(customerClass)))
            return CustomerUpdateResult.InvalidData;
        return await store.SetDoctorAsync(tenantId, actorId, new CustomerId(customerId), scope,
                new CustomerDoctorRequest(clinic, specialty, request.Cooperates, request.Dealer, customerClass), cancellationToken)
            ? CustomerUpdateResult.Success : CustomerUpdateResult.NotFound;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public async Task<CustomerUpdateResult> SetArchivedAsync(TenantId tenantId, UserId actorId, Guid customerId, bool archived, CancellationToken cancellationToken)
    {
        var scope = tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersApprove, cancellationToken);
        if (scope is null) return CustomerUpdateResult.Forbidden;
        if (customerId == Guid.Empty) return CustomerUpdateResult.InvalidData;
        return await store.SetArchivedAsync(tenantId, actorId, new CustomerId(customerId), scope, archived, cancellationToken)
            ? CustomerUpdateResult.Success : CustomerUpdateResult.NotFound;
    }
}
