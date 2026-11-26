using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Transfers;

// Goods moving between warehouses (plan step 5.60): the central warehouse to the cities, the cities back to the central one (returns, damaged goods) and city to city. Two steps:
// the sender's warehouse worker (or a manager) sends, which takes the goods out of the sender at once (status "sent"); the receiver's warehouse worker (or a manager) confirms what arrived,
// which adds it to the receiver ("received"); a smaller quantity than sent is written down on the line. The sender may cancel a transfer that has not arrived, which gives the goods back.
// A line is sellable stock ("sellable") or damaged / expired goods ("damaged"); damaged goods arrive as damaged goods. Only company warehouses take part.
public sealed record TransferItemRequest(Guid? ProductId, decimal? Quantity, string? Bucket);
// ClientRef makes a retry of a lost answer return the same transfer.
public sealed record CreateTransferRequest(Guid? FromWarehouseId, Guid? ToWarehouseId, Guid? ClientRef, string? Note, IReadOnlyList<TransferItemRequest>? Items);
public sealed record ReceiveLineRequest(int? LineNo, decimal? ReceivedQuantity);
// Lines: what arrived per line; a line that is left out arrives in full. Null = everything arrived in full.
public sealed record ReceiveTransferRequest(string? Note, IReadOnlyList<ReceiveLineRequest>? Lines = null);
public sealed record CancelTransferRequest(string? Note);

public sealed record TransferLineDto(int LineNo, Guid ProductId, string ProductCode, string ProductName, string Unit, string Bucket, decimal Quantity, decimal? ReceivedQuantity);
public sealed record TransferDto(Guid Id, string Number, Guid FromWarehouseId, string FromWarehouseCode, string FromWarehouseName, Guid ToWarehouseId, string ToWarehouseCode, string ToWarehouseName,
    string Status, string? Note, string? CreatedByEmail, DateTimeOffset CreatedAt, string? ClosedByEmail, DateTimeOffset? ClosedAt, string? CloseNote, IReadOnlyList<TransferLineDto> Items);
public sealed record CreatedTransferDto(Guid Id, string Number, bool Replayed);

public sealed record NewTransferLine(Guid ProductId, decimal Quantity, string Bucket);
public sealed record NewTransfer(Guid FromWarehouseId, Guid ToWarehouseId, Guid ClientRef, string? Note, IReadOnlyList<NewTransferLine> Lines);

// NotFound: no such transfer / warehouse / product (or a warehouse that is not an active company warehouse). Forbidden: not the worker of the needed warehouse. Conflict: the transfer is not
// "sent" any more (or the same reference was used for another transfer). TooMuch: more than the sender holds. InvalidData: a bad line.
public enum TransferResult { Success, Forbidden, NotFound, InvalidData, Conflict, TooMuch }

public interface ITransferStore
{
    // staffOnly: the user must work in the sender warehouse (null for a manager). Takes the goods out of the sender in the same transaction. Audit transfer.sent.
    Task<(TransferResult Result, CreatedTransferDto? Created)> CreateAsync(TenantId tenantId, UserId actorId, NewTransfer transfer, Guid? staffOnly, CancellationToken cancellationToken);
    // staffOnly: only transfers of warehouses the user works in (null = all). Newest first.
    Task<IReadOnlyList<TransferDto>> ListAsync(TenantId tenantId, string? status, Guid? warehouseId, Guid? staffOnly, int limit, CancellationToken cancellationToken);
    Task<TransferDto?> GetAsync(TenantId tenantId, Guid transferId, Guid? staffOnly, CancellationToken cancellationToken);
    // staffOnly: the user must work in the receiving warehouse (null for a manager). Adds the received quantities to the receiver. Audit transfer.received.
    Task<TransferResult> ReceiveAsync(TenantId tenantId, UserId actorId, Guid transferId, string? note, IReadOnlyDictionary<int, decimal> received, Guid? staffOnly, CancellationToken cancellationToken);
    // staffOnly: the user must work in the sending warehouse (null for a manager). Gives the goods back to the sender. Audit transfer.cancelled.
    Task<TransferResult> CancelAsync(TenantId tenantId, UserId actorId, Guid transferId, string? note, Guid? staffOnly, CancellationToken cancellationToken);
}

public sealed class TransferService(ITransferStore store, IPermissionStore permissions)
{
    public const int MaxLines = 100;
    public const decimal MaxQuantity = 9_999_999m;
    public const int MaxNote = 500;

    public async Task<(TransferResult Result, CreatedTransferDto? Created)> CreateAsync(TenantId tenantId, UserId actorId, CreateTransferRequest request, CancellationToken cancellationToken)
    {
        var who = await WhoAsync(tenantId, actorId, cancellationToken);
        if (who is null) return (TransferResult.Forbidden, null);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var items = request.Items;
        if (request.FromWarehouseId is null || request.ToWarehouseId is null || request.FromWarehouseId == Guid.Empty || request.ToWarehouseId == Guid.Empty ||
            request.FromWarehouseId == request.ToWarehouseId || request.ClientRef is null || request.ClientRef == Guid.Empty || note is { Length: > MaxNote } ||
            items is null || items.Count is 0 or > MaxLines ||
            items.Any(i => i.ProductId is null || i.ProductId == Guid.Empty || !ValidQuantity(i.Quantity) || i.Bucket is not ("sellable" or "damaged")) ||
            items.Select(i => (i.ProductId, i.Bucket)).Distinct().Count() != items.Count)
            return (TransferResult.InvalidData, null);
        var lines = items.Select(i => new NewTransferLine(i.ProductId!.Value, i.Quantity!.Value, i.Bucket!)).ToList();
        return await store.CreateAsync(tenantId, actorId, new NewTransfer(request.FromWarehouseId.Value, request.ToWarehouseId.Value, request.ClientRef.Value, note, lines), who.Value.Staff, cancellationToken);
    }

    public async Task<(TransferResult Result, IReadOnlyList<TransferDto> Items)> ListAsync(TenantId tenantId, UserId actorId, string? status, Guid? warehouseId, int? limit, CancellationToken cancellationToken)
    {
        var who = await WhoAsync(tenantId, actorId, cancellationToken);
        if (who is null) return (TransferResult.Forbidden, Array.Empty<TransferDto>());
        if (status is not (null or "" or "sent" or "received" or "cancelled")) return (TransferResult.InvalidData, Array.Empty<TransferDto>());
        return (TransferResult.Success, await store.ListAsync(tenantId, string.IsNullOrEmpty(status) ? null : status, warehouseId, who.Value.Staff, Math.Clamp(limit ?? 50, 1, 200), cancellationToken));
    }

    public async Task<(TransferResult Result, TransferDto? Item)> GetAsync(TenantId tenantId, UserId actorId, Guid transferId, CancellationToken cancellationToken)
    {
        var who = await WhoAsync(tenantId, actorId, cancellationToken);
        if (who is null) return (TransferResult.Forbidden, null);
        var item = transferId == Guid.Empty ? null : await store.GetAsync(tenantId, transferId, who.Value.Staff, cancellationToken);
        return item is null ? (TransferResult.NotFound, null) : (TransferResult.Success, item);
    }

    public async Task<TransferResult> ReceiveAsync(TenantId tenantId, UserId actorId, Guid transferId, ReceiveTransferRequest request, CancellationToken cancellationToken)
    {
        var who = await WhoAsync(tenantId, actorId, cancellationToken);
        if (who is null) return TransferResult.Forbidden;
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var lines = request.Lines ?? Array.Empty<ReceiveLineRequest>();
        if (transferId == Guid.Empty || note is { Length: > MaxNote } || lines.Count > MaxLines ||
            lines.Any(l => l.LineNo is null or < 1 || l.ReceivedQuantity is null or < 0 or > MaxQuantity || decimal.Round(l.ReceivedQuantity.Value, 3) != l.ReceivedQuantity) ||
            lines.Select(l => l.LineNo).Distinct().Count() != lines.Count)
            return TransferResult.InvalidData;
        return await store.ReceiveAsync(tenantId, actorId, transferId, note, lines.ToDictionary(l => l.LineNo!.Value, l => l.ReceivedQuantity!.Value), who.Value.Staff, cancellationToken);
    }

    public async Task<TransferResult> CancelAsync(TenantId tenantId, UserId actorId, Guid transferId, CancelTransferRequest request, CancellationToken cancellationToken)
    {
        var who = await WhoAsync(tenantId, actorId, cancellationToken);
        if (who is null) return TransferResult.Forbidden;
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (transferId == Guid.Empty || note is { Length: > MaxNote }) return TransferResult.InvalidData;
        return await store.CancelAsync(tenantId, actorId, transferId, note, who.Value.Staff, cancellationToken);
    }

    // A manager (warehouses.manage) works with every warehouse (Staff null); a warehouse worker (warehouse.work) only with the warehouses he works in (Staff = his id); anyone else: null.
    private async Task<(Guid? Staff, bool Manager)?> WhoAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return null;
        if (await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.WarehousesManage, cancellationToken)) return (null, true);
        if (await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.WarehouseWork, cancellationToken)) return (actorId.Value, false);
        return null;
    }

    private static bool ValidQuantity(decimal? quantity) => quantity is > 0 and <= MaxQuantity && decimal.Round(quantity.Value, 3) == quantity.Value;
}
