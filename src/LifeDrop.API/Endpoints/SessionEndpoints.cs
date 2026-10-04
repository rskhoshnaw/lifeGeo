using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Auth;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Kill Session (plan step 0.7, PRD §4): ends every session of a user immediately.
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapPost("/users/{userId:guid}/sessions/revoke", async (Guid userId, HttpContext context,
            KillUserSessions killSessions, CancellationToken cancellationToken) =>
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;
            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            var result = await killSessions.ExecuteAsync(new TenantId(tenantId), new UserId(actorId), new UserId(userId), cancellationToken);
            return result switch
            {
                KillSessionsResult.Success => Results.NoContent(),
                KillSessionsResult.UserNotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.UserNotFound),
                _ => Results.Forbid()
            };
        }).RequireAuthorization()
            .WithName("KillUserSessions").WithTags("Users").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
    }
}
