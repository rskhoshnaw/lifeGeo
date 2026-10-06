using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Push;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// The caller's own phone token (plan step 2.14): no permission is needed, everybody registers only for themselves.
public static class PushEndpoints
{
    public static void MapPushEndpoints(this WebApplication app)
    {
        app.MapPut("/push-token", async (PushTokenRequest request, HttpContext context, PushTokenService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.RegisterAsync(tenantId, actorId, request.Token, request.Platform, cancellationToken);
            return result == PushResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("RegisterPushToken").WithTags("Push").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/push-token/remove", async (PushTokenRemoveRequest request, HttpContext context, PushTokenService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.RemoveAsync(tenantId, actorId, request.Token, cancellationToken);
            return result == PushResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("RemovePushToken").WithTags("Push").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);
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

    private static IResult ToError(PushResult result) => result switch
    {
        PushResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        PushResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        _ => Results.Forbid()
    };
}

public sealed record PushTokenRequest(string? Token, string? Platform);
public sealed record PushTokenRemoveRequest(string? Token);
