using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Catalog;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Orders;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;

namespace LifeDrop.Application.Samples;

// Pharma samples (plan step 2.6). A distribution = a catalogue product, a quantity, a customer (pharmacy/clinic) inside the caller's area, optionally the
// visit; the recipient's name, batch number and expiry date are optional (an expired sample is refused); the phone's position is optional evidence (a fake
// one is refused). No stock control yet. Nothing is rewritten: a manager voids a record with a reason.
public sealed record SampleDto(Guid Id, string Number, Guid CustomerId, string CustomerCode, string CustomerName, Guid? VisitId, Guid ProductId, string ProductCode, string ProductName,
    string Unit, decimal Quantity, string? RecipientName, string? BatchNumber, DateOnly? ExpiryDate, string? Note, Guid DistributedBy, string? DistributedByEmail, double? Latitude,
    double? Longitude, string Status, string? VoidReason, DateTimeOffset CreatedAt);

public sealed record RecordSampleRequest(Guid? CustomerId, Guid? VisitId, Guid? ProductId, decimal? Quantity, string? RecipientName, string? BatchNumber, DateOnly? ExpiryDate,
    string? Note, Guid? ClientRef, DeliveryLocation? Location = null, bool? DetachVisitIfInvalid = null);
public sealed record VoidSampleRequest(string? Reason);
// Detached: the visit was cancelled or failed by the time a sample saved without internet arrived (plan step 2.13); the sample is recorded without the visit.
public sealed record CreatedSampleDto(Guid Id, string Number, bool Replayed, bool Detached = false);

public enum SampleResult { Success, Forbidden, NotFound, InvalidData, Conflict, GpsMocked }
public enum SampleView { Everything, Region, Mine }

public sealed record NewSample(Guid CustomerId, Guid? VisitId, Guid ProductId, decimal Quantity, string? RecipientName, string? BatchNumber, DateOnly? ExpiryDate, string? Note,
    Guid ClientRef, DeliveryLocation? Location);

public interface ISampleStore
{
    // NotFound: the product is missing. InvalidData: the product is inactive. Replays an earlier send with the same ClientRef (same user and customer, else Conflict).
    Task<(SampleResult Result, CreatedSampleDto? Sample)> CreateAsync(TenantId tenantId, UserId actorId, NewSample sample, CancellationToken cancellationToken);
    Task<IReadOnlyList<SampleDto>> ListAsync(TenantId tenantId, UserId actorId, SampleView view, Guid? regionId, Guid? customerId, Guid? productId, int limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken);
    Task<SampleDto?> GetAsync(TenantId tenantId, UserId actorId, SampleView view, Guid? regionId, Guid sampleId, CancellationToken cancellationToken);
    // given -> voided; Conflict when it is voided already.
    Task<SampleResult> VoidAsync(TenantId tenantId, UserId actorId, Guid sampleId, string reason, CancellationToken cancellationToken);
}

public sealed class SampleService(ISampleStore store, IPermissionStore permissions, ICustomerStore customers, IVisitStore visits, TimeProvider clock)
{
    public const decimal MaxQuantity = 100_000m;

    public async Task<(SampleResult Result, CreatedSampleDto? Sample)> RecordAsync(TenantId tenantId, UserId actorId, RecordSampleRequest request, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.SamplesRecord, cancellationToken);
        if (scope is null) return (SampleResult.Forbidden, null);
        var recipient = Trim(request.RecipientName);
        var batch = Trim(request.BatchNumber);
        var note = Trim(request.Note);
        if (request.CustomerId is null || request.CustomerId == Guid.Empty || request.ProductId is null || request.ProductId == Guid.Empty || request.VisitId == Guid.Empty ||
            request.ClientRef is null || request.ClientRef == Guid.Empty || request.Quantity is not > 0 || request.Quantity > MaxQuantity || decimal.Round(request.Quantity.Value, 3) != request.Quantity ||
            recipient?.Length > 100 || batch?.Length > 50 || note?.Length > 500 || (request.ExpiryDate is { } expiry && expiry < IraqCalendar.Today(clock)))
            return (SampleResult.InvalidData, null);
        var location = CheckLocation(request.Location, out var problem);
        if (problem is not null) return (problem.Value, null);

        var customer = await customers.GetByIdAsync(tenantId, new CustomerId(request.CustomerId.Value), scope, cancellationToken);
        if (customer is null) return (SampleResult.NotFound, null);
        var visitIdToUse = request.VisitId;
        var detached = false;
        if (request.VisitId is { } visitId)
        {
            var visit = await visits.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
            var visitProblem = visit is null || visit.CustomerId.Value != customer.Id || visit.UserId != actorId ? SampleResult.NotFound
                : visit.Status is VisitStatus.Cancelled or VisitStatus.Failed ? SampleResult.InvalidData : SampleResult.Success;
            if (visitProblem != SampleResult.Success)
            {
                if (request.DetachVisitIfInvalid != true) return (visitProblem, null);
                visitIdToUse = null;
                detached = true;
            }
        }
        var (result, created) = await store.CreateAsync(tenantId, actorId, new NewSample(customer.Id, visitIdToUse, request.ProductId.Value, request.Quantity.Value, recipient, batch,
            request.ExpiryDate, note, request.ClientRef.Value, location), cancellationToken);
        return (result, created is not null && detached ? created with { Detached = true } : created);
    }

    public async Task<(SampleResult Result, IReadOnlyList<SampleDto> Items)> ListAsync(TenantId tenantId, UserId actorId, Guid? customerId, Guid? productId, int? limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        var (view, region) = await ViewAsync(tenantId, actorId, cancellationToken);
        if (view is null) return (SampleResult.Forbidden, Array.Empty<SampleDto>());
        var take = limit ?? 50;
        if (take is < 1 or > 100 || (before is null) != (beforeId is null) || customerId == Guid.Empty || productId == Guid.Empty) return (SampleResult.InvalidData, Array.Empty<SampleDto>());
        return (SampleResult.Success, await store.ListAsync(tenantId, actorId, view.Value, region, customerId, productId, take, before, beforeId, cancellationToken));
    }

    public async Task<(SampleResult Result, SampleDto? Sample)> GetAsync(TenantId tenantId, UserId actorId, Guid sampleId, CancellationToken cancellationToken)
    {
        var (view, region) = await ViewAsync(tenantId, actorId, cancellationToken);
        if (view is null) return (SampleResult.Forbidden, null);
        var sample = sampleId == Guid.Empty ? null : await store.GetAsync(tenantId, actorId, view.Value, region, sampleId, cancellationToken);
        return sample is null ? (SampleResult.NotFound, null) : (SampleResult.Success, sample);
    }

    public async Task<SampleResult> VoidAsync(TenantId tenantId, UserId actorId, Guid sampleId, VoidSampleRequest request, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.SamplesManage, cancellationToken) is null) return SampleResult.Forbidden;
        var reason = Trim(request.Reason);
        if (sampleId == Guid.Empty || string.IsNullOrEmpty(reason) || reason.Length > 500) return SampleResult.InvalidData;
        return await store.VoidAsync(tenantId, actorId, sampleId, reason, cancellationToken);
    }

    // Everything for tenant-wide readers, the region for supervisors; a user who only records sees what they recorded.
    private async Task<(SampleView? View, Guid? Region)> ViewAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        var read = await ScopeAsync(tenantId, actorId, PermissionKeys.SamplesRead, cancellationToken);
        if (read is not null) return read.Scope == DataScope.Tenant ? (SampleView.Everything, null) : read.RegionId is { } r ? (SampleView.Region, r) : (SampleView.Mine, null);
        return await ScopeAsync(tenantId, actorId, PermissionKeys.SamplesRecord, cancellationToken) is not null ? (SampleView.Mine, null) : (null, null);
    }

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // The position is evidence, so it is optional; but it must be a real coordinate and not a position the phone says is fake.
    private static DeliveryLocation? CheckLocation(DeliveryLocation? location, out SampleResult? problem)
    {
        problem = null;
        if (location is null || (location.Latitude is null && location.Longitude is null)) return null;
        if (location.IsMocked) { problem = SampleResult.GpsMocked; return null; }
        if (location.Latitude is not (>= -90 and <= 90) || location.Longitude is not (>= -180 and <= 180) || location.AccuracyMeters is < 0 or > 100_000 ||
            double.IsNaN(location.Latitude.Value) || double.IsNaN(location.Longitude.Value))
        {
            problem = SampleResult.InvalidData;
            return null;
        }
        return location;
    }

    private Task<AccessScope?> ScopeAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? Task.FromResult<AccessScope?>(null) : permissions.GetAccessScopeAsync(tenantId, actorId, permission, cancellationToken);
}
