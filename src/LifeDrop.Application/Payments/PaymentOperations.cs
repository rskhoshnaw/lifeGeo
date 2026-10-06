using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Payments;

// Payments (plan step 2.5). A payment belongs to a customer and optionally to one of its orders (same currency). Methods: cash, fastpay, fib, other
// (FastPay and FIB need the reference). One currency per payment (IQD or USD). The finance manager records a payment as "confirmed"; a visitor who
// takes the money records it as "collected" until the finance manager confirms that he received it. Nothing is rewritten: a payment is voided (with
// a reason) or corrected, which writes a new payment that points at the old one and marks the old one "superseded". Every change is in the history
// and in the audit log.
public sealed record PaymentHistoryDto(string Status, string? ActorEmail, string? Note, DateTimeOffset At);

// Number: "P-000001". Status: collected | confirmed | voided | superseded. ReplacedBy: the payment that corrected this one.
public sealed record PaymentDto(Guid Id, string Number, Guid CustomerId, string CustomerCode, string CustomerName, Guid? OrderId, string? OrderNumber, string Method,
    string Currency, decimal Amount, string? Reference, string? Note, string Status, Guid ReceivedBy, string? ReceivedByEmail, Guid RecordedBy, string? ConfirmedByEmail,
    DateTimeOffset? ConfirmedAt, string? ClosedReason, Guid? ReplacesPaymentId, Guid? ReplacedByPaymentId, DateTimeOffset CreatedAt, IReadOnlyList<PaymentHistoryDto>? History = null);

public sealed record RecordPaymentRequest(Guid? CustomerId, Guid? OrderId, string? Method, string? Currency, decimal? Amount, string? Reference, string? Note, Guid? ClientRef);
public sealed record CorrectPaymentRequest(Guid? OrderId, string? Method, string? Currency, decimal? Amount, string? Reference, string? Note, string? Reason);
public sealed record ClosePaymentRequest(string? Reason);

// Replayed: the same ClientRef was sent before and the payment that exists is returned.
public sealed record CreatedPaymentDto(Guid Id, string Number, string Status, bool Replayed);

// Delivered: the value of what was delivered (delivered quantity x unit price of delivered orders). Confirmed: payments the finance manager has. Collected:
// money a visitor took and has not given in yet. Balance = Delivered - Confirmed (what the customer still owes).
public sealed record BalanceItemDto(string Currency, decimal Delivered, decimal Confirmed, decimal Collected, decimal Balance);
public sealed record CustomerBalanceDto(Guid CustomerId, IReadOnlyList<BalanceItemDto> Items);

public enum PaymentResult { Success, Forbidden, NotFound, InvalidData, Conflict }

// How much of the payments a user may see: everything, a region (and the user's own), or only what the user recorded.
public enum PaymentView { Everything, Region, Mine }

public sealed record NewPayment(Guid CustomerId, Guid? OrderId, string Method, string Currency, decimal Amount, string? Reference, string? Note, Guid ClientRef);

public interface IPaymentStore
{
    // NotFound: order missing. InvalidData: the order is another customer's, cancelled, or in another currency. Replays an earlier send with the same
    // ClientRef (same recorder and customer; else Conflict). Number, history and audit row in one transaction.
    Task<(PaymentResult Result, CreatedPaymentDto? Payment)> CreateAsync(TenantId tenantId, UserId actorId, NewPayment payment, string status, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaymentDto>> ListAsync(TenantId tenantId, UserId actorId, PaymentView view, Guid? regionId, Guid? customerId, string? status, int limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken);
    Task<PaymentDto?> GetAsync(TenantId tenantId, UserId actorId, PaymentView view, Guid? regionId, Guid paymentId, CancellationToken cancellationToken);
    // collected -> confirmed. Conflict: another status. NotFound: no such payment.
    Task<PaymentResult> ConfirmAsync(TenantId tenantId, UserId actorId, Guid paymentId, CancellationToken cancellationToken);
    // collected or confirmed -> voided. A user who is not a manager voids only a payment they recorded that is still collected (else Forbidden).
    Task<PaymentResult> VoidAsync(TenantId tenantId, UserId actorId, Guid paymentId, string reason, bool isManager, CancellationToken cancellationToken);
    // collected or confirmed -> superseded, and a new confirmed payment for the same customer that replaces it.
    Task<(PaymentResult Result, CreatedPaymentDto? Payment)> CorrectAsync(TenantId tenantId, UserId actorId, Guid paymentId, NewPayment replacement, string reason,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<BalanceItemDto>> BalanceAsync(TenantId tenantId, Guid customerId, CancellationToken cancellationToken);
}

public sealed class PaymentService(IPaymentStore store, IPermissionStore permissions, ICustomerStore customers)
{
    public const decimal MaxAmount = 999_999_999_999m;
    public static readonly string[] Methods = ["cash", "fastpay", "fib", "other"];
    public static readonly string[] Currencies = ["IQD", "USD"];

    public async Task<(PaymentResult Result, CreatedPaymentDto? Payment)> RecordAsync(TenantId tenantId, UserId actorId, RecordPaymentRequest request, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.PaymentsRecord, cancellationToken);
        if (scope is null) return (PaymentResult.Forbidden, null);
        if (request.CustomerId is null || request.CustomerId == Guid.Empty || request.OrderId == Guid.Empty || request.ClientRef is null || request.ClientRef == Guid.Empty ||
            !Clean(request.Method, request.Currency, request.Amount, request.Reference, request.Note, out var payment, request.CustomerId.Value, request.OrderId, request.ClientRef.Value))
            return (PaymentResult.InvalidData, null);
        // The customer must be inside the caller's area.
        if (await customers.GetByIdAsync(tenantId, new CustomerId(request.CustomerId.Value), scope, cancellationToken) is null) return (PaymentResult.NotFound, null);
        // The finance manager's payment counts at once; a visitor's waits for the finance manager.
        var manager = await ScopeAsync(tenantId, actorId, PermissionKeys.PaymentsManage, cancellationToken) is not null;
        return await store.CreateAsync(tenantId, actorId, payment!, manager ? "confirmed" : "collected", cancellationToken);
    }

    public async Task<(PaymentResult Result, IReadOnlyList<PaymentDto> Items)> ListAsync(TenantId tenantId, UserId actorId, Guid? customerId, string? status, int? limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        var (view, region) = await ViewAsync(tenantId, actorId, cancellationToken);
        if (view is null) return (PaymentResult.Forbidden, Array.Empty<PaymentDto>());
        return await ListInternalAsync(tenantId, actorId, view.Value, region, customerId, status, limit, before, beforeId, cancellationToken);
    }

    // What the user recorded (a visitor sees their own payments and whether the finance manager has confirmed them).
    public async Task<(PaymentResult Result, IReadOnlyList<PaymentDto> Items)> ListMineAsync(TenantId tenantId, UserId actorId, string? status, int? limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.PaymentsRecord, cancellationToken) is null) return (PaymentResult.Forbidden, Array.Empty<PaymentDto>());
        return await ListInternalAsync(tenantId, actorId, PaymentView.Mine, null, null, status, limit, before, beforeId, cancellationToken);
    }

    private async Task<(PaymentResult Result, IReadOnlyList<PaymentDto> Items)> ListInternalAsync(TenantId tenantId, UserId actorId, PaymentView view, Guid? region, Guid? customerId,
        string? status, int? limit, DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        var take = limit ?? 50;
        if (take is < 1 or > 100 || (before is null) != (beforeId is null) || customerId == Guid.Empty || status is not (null or "collected" or "confirmed" or "voided" or "superseded"))
            return (PaymentResult.InvalidData, Array.Empty<PaymentDto>());
        return (PaymentResult.Success, await store.ListAsync(tenantId, actorId, view, region, customerId, status, take, before, beforeId, cancellationToken));
    }

    public async Task<(PaymentResult Result, PaymentDto? Payment)> GetAsync(TenantId tenantId, UserId actorId, Guid paymentId, CancellationToken cancellationToken)
    {
        var (view, region) = await ViewAsync(tenantId, actorId, cancellationToken);
        if (view is null) return (PaymentResult.Forbidden, null);
        var payment = paymentId == Guid.Empty ? null : await store.GetAsync(tenantId, actorId, view.Value, region, paymentId, cancellationToken);
        return payment is null ? (PaymentResult.NotFound, null) : (PaymentResult.Success, payment);
    }

    public async Task<PaymentResult> ConfirmAsync(TenantId tenantId, UserId actorId, Guid paymentId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.PaymentsManage, cancellationToken) is null) return PaymentResult.Forbidden;
        return paymentId == Guid.Empty ? PaymentResult.InvalidData : await store.ConfirmAsync(tenantId, actorId, paymentId, cancellationToken);
    }

    public async Task<PaymentResult> VoidAsync(TenantId tenantId, UserId actorId, Guid paymentId, ClosePaymentRequest request, CancellationToken cancellationToken)
    {
        var manager = await ScopeAsync(tenantId, actorId, PermissionKeys.PaymentsManage, cancellationToken) is not null;
        if (!manager && await ScopeAsync(tenantId, actorId, PermissionKeys.PaymentsRecord, cancellationToken) is null) return PaymentResult.Forbidden;
        var reason = request.Reason?.Trim();
        if (paymentId == Guid.Empty || string.IsNullOrEmpty(reason) || reason.Length > 500) return PaymentResult.InvalidData;
        return await store.VoidAsync(tenantId, actorId, paymentId, reason, manager, cancellationToken);
    }

    public async Task<(PaymentResult Result, CreatedPaymentDto? Payment)> CorrectAsync(TenantId tenantId, UserId actorId, Guid paymentId, CorrectPaymentRequest request,
        CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.PaymentsManage, cancellationToken) is null) return (PaymentResult.Forbidden, null);
        var reason = request.Reason?.Trim();
        if (paymentId == Guid.Empty || request.OrderId == Guid.Empty || string.IsNullOrEmpty(reason) || reason.Length > 500 ||
            !Clean(request.Method, request.Currency, request.Amount, request.Reference, request.Note, out var replacement, Guid.Empty, request.OrderId, Guid.NewGuid()))
            return (PaymentResult.InvalidData, null);
        return await store.CorrectAsync(tenantId, actorId, paymentId, replacement!, reason, cancellationToken);
    }

    // The balance of a customer in the caller's area: payments.read or payments.record.
    public async Task<(PaymentResult Result, CustomerBalanceDto? Balance)> BalanceAsync(TenantId tenantId, UserId actorId, Guid customerId, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.PaymentsRead, cancellationToken) ?? await ScopeAsync(tenantId, actorId, PermissionKeys.PaymentsRecord, cancellationToken);
        if (scope is null) return (PaymentResult.Forbidden, null);
        if (customerId == Guid.Empty) return (PaymentResult.InvalidData, null);
        if (await customers.GetByIdAsync(tenantId, new CustomerId(customerId), scope, cancellationToken) is null) return (PaymentResult.NotFound, null);
        return (PaymentResult.Success, new CustomerBalanceDto(customerId, await store.BalanceAsync(tenantId, customerId, cancellationToken)));
    }

    // Everything for tenant-wide readers, the region for supervisors; a user who only records sees what they recorded.
    private async Task<(PaymentView? View, Guid? Region)> ViewAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        var read = await ScopeAsync(tenantId, actorId, PermissionKeys.PaymentsRead, cancellationToken);
        if (read is not null) return read.Scope == DataScope.Tenant ? (PaymentView.Everything, null) : read.RegionId is { } r ? (PaymentView.Region, r) : (PaymentView.Mine, null);
        return await ScopeAsync(tenantId, actorId, PermissionKeys.PaymentsRecord, cancellationToken) is not null ? (PaymentView.Mine, null) : (null, null);
    }

    private Task<AccessScope?> ScopeAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? Task.FromResult<AccessScope?>(null) : permissions.GetAccessScopeAsync(tenantId, actorId, permission, cancellationToken);

    private static bool Clean(string? method, string? currency, decimal? amount, string? reference, string? note, out NewPayment? payment, Guid customerId, Guid? orderId, Guid clientRef)
    {
        payment = null;
        var reference1 = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        var note1 = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (method is null || !Methods.Contains(method) || currency is null || !Currencies.Contains(currency) || amount is not > 0 or null || amount > MaxAmount ||
            decimal.Round(amount.Value, 3) != amount || reference1?.Length > 100 || note1?.Length > 500 || (method is "fastpay" or "fib" && reference1 is null))
            return false;
        payment = new NewPayment(customerId, orderId, method, currency, amount.Value, reference1, note1, clientRef);
        return true;
    }
}
