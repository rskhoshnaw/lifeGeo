using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Audit;

// Read-only audit search (plan step 0.8, PRD §3, §12). Audit rows are never changed or deleted
// through the product; the table itself rejects UPDATE/DELETE/TRUNCATE.
// ActorEmail (plan step 1.20) lets the panel show who acted without another request.
public sealed record AuditLogEntryDto(Guid Id, Guid ActorId, string Action, string EntityId, DateTimeOffset OccurredAt, string? ActorEmail = null);

public sealed record AuditLogQuery(
    string? Action, Guid? ActorId, string? EntityId, DateTimeOffset? From, DateTimeOffset? To,
    int? Limit, DateTimeOffset? CursorOccurredAt, Guid? CursorId);

public enum AuditSearchResult { Success, Forbidden, InvalidData }

public interface IAuditLogStore
{
    // Newest first; the cursor is the last row of the previous page.
    Task<IReadOnlyList<AuditLogEntryDto>> SearchAsync(TenantId tenantId, AccessScope scope, AuditLogQuery query, int limit, CancellationToken cancellationToken);
}

public sealed class SearchAuditLogs(IAuditLogStore store, IPermissionStore permissions)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    public async Task<(AuditSearchResult Result, IReadOnlyList<AuditLogEntryDto> Items)> ExecuteAsync(TenantId tenantId, UserId actorId, AuditLogQuery query, CancellationToken cancellationToken)
    {
        var none = Array.Empty<AuditLogEntryDto>();
        if (tenantId.IsEmpty || actorId.IsEmpty) return (AuditSearchResult.Forbidden, none);

        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.AuditRead, cancellationToken);
        if (scope is null) return (AuditSearchResult.Forbidden, none);

        var limit = query.Limit ?? DefaultLimit;
        if (limit < 1 || limit > MaxLimit) return (AuditSearchResult.InvalidData, none);
        if (query.CursorOccurredAt.HasValue != query.CursorId.HasValue) return (AuditSearchResult.InvalidData, none);
        if (query.From > query.To) return (AuditSearchResult.InvalidData, none);
        if (query.Action?.Length > 100 || query.EntityId?.Length > 200) return (AuditSearchResult.InvalidData, none);

        return (AuditSearchResult.Success, await store.SearchAsync(tenantId, scope, query, limit, cancellationToken));
    }
}
