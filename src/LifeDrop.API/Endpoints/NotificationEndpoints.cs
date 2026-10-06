using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Notifications;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// The caller's own messages (plan step 1.23-c): no permission is needed, everybody reads only their own.
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this WebApplication app)
    {
        app.MapGet("/notifications", async (bool? unread, int? limit, HttpContext context, NotificationService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, unread == true, limit, cancellationToken);
            return result == NotificationResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListNotifications").WithTags("Notifications").Produces<List<NotificationDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/notifications/unread-count", async (HttpContext context, NotificationService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, count) = await service.CountUnreadAsync(tenantId, actorId, cancellationToken);
            return result == NotificationResult.Success ? Results.Ok(new UnreadCountResponse(count)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CountUnreadNotifications").WithTags("Notifications").Produces<UnreadCountResponse>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/notifications/{notificationId:guid}/read", async (Guid notificationId, HttpContext context, NotificationService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.MarkReadAsync(tenantId, actorId, notificationId, cancellationToken);
            return result == NotificationResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("MarkNotificationRead").WithTags("Notifications").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/notifications/read-all", async (HttpContext context, NotificationService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, _) = await service.MarkAllReadAsync(tenantId, actorId, cancellationToken);
            return result == NotificationResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("MarkAllNotificationsRead").WithTags("Notifications").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
    }

    private static bool TryReadActor(HttpContext context, out TenantId tenantId, out UserId actorId)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var tenant = context.User.FindFirst("tenant_id")?.Value;
        var valid = Guid.TryParse(subject, out var actor) && actor != Guid.Empty &&
                    Guid.TryParse(tenant, out var tenantGuid) && tenantGuid != Guid.Empty;
        tenantId = valid ? new TenantId(Guid.Parse(tenant!)) : default;
        actorId = valid ? new UserId(actor) : default;
        return valid;
    }

    private static IResult Unavailable() => ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);

    private static IResult ToError(NotificationResult result) => result switch
    {
        NotificationResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        NotificationResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        _ => Results.Forbid()
    };
}

public sealed record UnreadCountResponse(int Count);
