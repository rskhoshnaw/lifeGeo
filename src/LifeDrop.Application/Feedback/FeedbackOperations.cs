using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Feedback;

// Feedback from the field (plan step 5.30, owner request 2026-11-11): a suggestion, a complaint or a note, optionally about a customer, with up to 3 photos. Everybody sends and
// reads their own and the managers' answers. A user with feedback.manage (CompanyManager, SalesManager: the whole company; Supervisor: the own region) reads the feedback in their data
// scope, answers it and sets its status. A new feedback notifies those managers (kind feedback.new), an answer notifies the author (feedback.reply).
public static class FeedbackKinds
{
    public static readonly IReadOnlyList<string> Kinds = ["suggestion", "complaint", "note"];
    public static readonly IReadOnlyList<string> Statuses = ["new", "in_review", "done"];
    public static readonly IReadOnlyList<string> PhotoTypes = ["image/jpeg", "image/png", "image/webp"];
}

public sealed record CreateFeedbackRequest(string? Kind, string? Body, Guid? CustomerId = null);
public sealed record ReplyFeedbackRequest(string? Body);
public sealed record SetFeedbackStatusRequest(string? Status);
public sealed record FeedbackCreatedDto(Guid Id);

public sealed record FeedbackReplyDto(Guid Id, Guid UserId, string? UserEmail, string Body, DateTimeOffset CreatedAt);

// PhotoIds are fetched one by one (GET /feedback/{id}/photos/{photoId}); Replies are oldest first.
public sealed record FeedbackDto(Guid Id, string Kind, string Body, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid UserId, string? UserEmail,
    Guid? CustomerId, string? CustomerCode, string? CustomerName, IReadOnlyList<Guid> PhotoIds, IReadOnlyList<FeedbackReplyDto> Replies);

public sealed record FeedbackPhoto(string ContentType, byte[] Data);

// MineOnly: only the caller's own feedback (always the case without feedback.manage). The other filters narrow the list; Before pages backwards (newest first).
public sealed record FeedbackFilter(bool MineOnly, string? Status, string? Kind, Guid? UserId, Guid? CustomerId, DateTimeOffset? Before, int Limit);

// What the caller may reach: own feedback always, and the data scope of feedback.manage when they have it.
public sealed record FeedbackAccess(UserId ActorId, AccessScope? Manage);

public enum FeedbackResult { Success, Forbidden, NotFound, InvalidData, TooManyPhotos }

public interface IFeedbackStore
{
    // Null when the customer is not one the author may see (customerScope is the author's scope of customers.read; null = no customer may be linked).
    Task<Guid?> CreateAsync(TenantId tenantId, UserId author, string kind, string body, Guid? customerId, AccessScope? customerScope, CancellationToken cancellationToken);
    Task<IReadOnlyList<FeedbackDto>> ListAsync(TenantId tenantId, FeedbackAccess access, FeedbackFilter filter, CancellationToken cancellationToken);
    Task<FeedbackDto?> GetAsync(TenantId tenantId, FeedbackAccess access, Guid id, CancellationToken cancellationToken);
    // Only the author adds photos. Success, NotFound (not theirs) or TooManyPhotos.
    Task<FeedbackResult> AddPhotoAsync(TenantId tenantId, UserId author, Guid id, string contentType, byte[] data, int maxPhotos, CancellationToken cancellationToken);
    Task<FeedbackPhoto?> GetPhotoAsync(TenantId tenantId, FeedbackAccess access, Guid id, Guid photoId, CancellationToken cancellationToken);
    // A manager in scope answers: the first answer moves a new feedback to in_review; the author is notified (unless they wrote the answer). False when not found in scope.
    Task<bool> AddReplyAsync(TenantId tenantId, UserId actor, AccessScope manage, Guid id, string body, CancellationToken cancellationToken);
    Task<bool> SetStatusAsync(TenantId tenantId, UserId actor, AccessScope manage, Guid id, string status, CancellationToken cancellationToken);
}

public sealed class FeedbackService(IFeedbackStore store, IPermissionStore permissions)
{
    public const int MaxBody = 2000;
    public const int MaxReply = 1000;
    public const int MaxPhotos = 3;
    public const int MaxPhotoBytes = 3 * 1024 * 1024;
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    private static bool Ok(TenantId tenantId, UserId actorId) => !tenantId.IsEmpty && !actorId.IsEmpty;

    private async Task<FeedbackAccess> AccessAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        new(actorId, await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.FeedbackManage, cancellationToken));

    // Everybody may write. A customer can be linked only when it is one the author may see.
    public async Task<(FeedbackResult Result, FeedbackCreatedDto? Data)> CreateAsync(TenantId tenantId, UserId actorId, CreateFeedbackRequest request, CancellationToken cancellationToken)
    {
        if (!Ok(tenantId, actorId)) return (FeedbackResult.Forbidden, null);
        var kind = request.Kind?.Trim().ToLowerInvariant();
        var body = request.Body?.Trim();
        if (kind is null || !FeedbackKinds.Kinds.Contains(kind) || string.IsNullOrEmpty(body) || body.Length > MaxBody || request.CustomerId == Guid.Empty)
            return (FeedbackResult.InvalidData, null);
        AccessScope? customerScope = null;
        if (request.CustomerId is not null)
        {
            customerScope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken);
            if (customerScope is null) return (FeedbackResult.InvalidData, null);
        }
        var id = await store.CreateAsync(tenantId, actorId, kind, body, request.CustomerId, customerScope, cancellationToken);
        return id is { } created ? (FeedbackResult.Success, new FeedbackCreatedDto(created)) : (FeedbackResult.InvalidData, null);
    }

    public async Task<(FeedbackResult Result, IReadOnlyList<FeedbackDto> Items)> ListAsync(TenantId tenantId, UserId actorId, bool? mine, string? status, string? kind, Guid? userId,
        Guid? customerId, DateTimeOffset? before, int? limit, CancellationToken cancellationToken)
    {
        if (!Ok(tenantId, actorId)) return (FeedbackResult.Forbidden, []);
        var st = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        var kd = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant();
        var count = limit ?? DefaultLimit;
        if ((st is not null && !FeedbackKinds.Statuses.Contains(st)) || (kd is not null && !FeedbackKinds.Kinds.Contains(kd)) || userId == Guid.Empty || customerId == Guid.Empty || count is < 1 or > MaxLimit)
            return (FeedbackResult.InvalidData, []);
        var access = await AccessAsync(tenantId, actorId, cancellationToken);
        var onlyMine = mine == true || access.Manage is null;
        return (FeedbackResult.Success, await store.ListAsync(tenantId, access, new FeedbackFilter(onlyMine, st, kd, userId, customerId, before, count), cancellationToken));
    }

    public async Task<(FeedbackResult Result, FeedbackDto? Data)> GetAsync(TenantId tenantId, UserId actorId, Guid id, CancellationToken cancellationToken)
    {
        if (!Ok(tenantId, actorId) || id == Guid.Empty) return (FeedbackResult.NotFound, null);
        var item = await store.GetAsync(tenantId, await AccessAsync(tenantId, actorId, cancellationToken), id, cancellationToken);
        return item is null ? (FeedbackResult.NotFound, null) : (FeedbackResult.Success, item);
    }

    public async Task<FeedbackResult> AddPhotoAsync(TenantId tenantId, UserId actorId, Guid id, string? contentType, byte[] data, CancellationToken cancellationToken)
    {
        if (!Ok(tenantId, actorId) || id == Guid.Empty) return FeedbackResult.NotFound;
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (type is null || !FeedbackKinds.PhotoTypes.Contains(type) || data.Length is < 1 or > MaxPhotoBytes) return FeedbackResult.InvalidData;
        return await store.AddPhotoAsync(tenantId, actorId, id, type, data, MaxPhotos, cancellationToken);
    }

    public async Task<(FeedbackResult Result, FeedbackPhoto? Photo)> GetPhotoAsync(TenantId tenantId, UserId actorId, Guid id, Guid photoId, CancellationToken cancellationToken)
    {
        if (!Ok(tenantId, actorId) || id == Guid.Empty || photoId == Guid.Empty) return (FeedbackResult.NotFound, null);
        var photo = await store.GetPhotoAsync(tenantId, await AccessAsync(tenantId, actorId, cancellationToken), id, photoId, cancellationToken);
        return photo is null ? (FeedbackResult.NotFound, null) : (FeedbackResult.Success, photo);
    }

    // Needs feedback.manage; the feedback must be inside the data scope (else it is not found).
    public async Task<FeedbackResult> ReplyAsync(TenantId tenantId, UserId actorId, Guid id, ReplyFeedbackRequest request, CancellationToken cancellationToken)
    {
        if (!Ok(tenantId, actorId)) return FeedbackResult.Forbidden;
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.FeedbackManage, cancellationToken);
        if (scope is null) return FeedbackResult.Forbidden;
        var body = request.Body?.Trim();
        if (id == Guid.Empty || string.IsNullOrEmpty(body) || body.Length > MaxReply) return FeedbackResult.InvalidData;
        return await store.AddReplyAsync(tenantId, actorId, scope, id, body, cancellationToken) ? FeedbackResult.Success : FeedbackResult.NotFound;
    }

    public async Task<FeedbackResult> SetStatusAsync(TenantId tenantId, UserId actorId, Guid id, SetFeedbackStatusRequest request, CancellationToken cancellationToken)
    {
        if (!Ok(tenantId, actorId)) return FeedbackResult.Forbidden;
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.FeedbackManage, cancellationToken);
        if (scope is null) return FeedbackResult.Forbidden;
        var status = request.Status?.Trim().ToLowerInvariant();
        if (id == Guid.Empty || status is null || !FeedbackKinds.Statuses.Contains(status)) return FeedbackResult.InvalidData;
        return await store.SetStatusAsync(tenantId, actorId, scope, id, status, cancellationToken) ? FeedbackResult.Success : FeedbackResult.NotFound;
    }
}
