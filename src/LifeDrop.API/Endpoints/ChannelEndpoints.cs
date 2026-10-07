using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Channels;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// The caller's own message channel (plan step 3.4, D17): no permission is needed, everybody links only their own Telegram chat. Linking is finished in Telegram:
// the user sends the one-time code to the company bot (see docs/CHANNELS.md).
public static class ChannelEndpoints
{
    public static void MapChannelEndpoints(this WebApplication app)
    {
        app.MapGet("/channels", async (HttpContext context, ChannelLinkService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, status) = await service.StatusAsync(tenantId, actorId, cancellationToken);
            return result == ChannelResult.Success ? Results.Ok(status) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetChannels").WithTags("Channels").Produces<ChannelStatus>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/channels/telegram/link", async (HttpContext context, ChannelLinkService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, link) = await service.StartTelegramLinkAsync(tenantId, actorId, cancellationToken);
            return result == ChannelResult.Success ? Results.Ok(link) : ToError(result);
        }).RequireAuthorization()
            .WithName("StartTelegramLink").WithTags("Channels").Produces<TelegramLink>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/channels/telegram/unlink", async (HttpContext context, ChannelLinkService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.UnlinkTelegramAsync(tenantId, actorId, cancellationToken);
            return result == ChannelResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("UnlinkTelegram").WithTags("Channels").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);
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

    // Channels turned off or without a bot token: the same answer as a service that is not there.
    private static IResult ToError(ChannelResult result) => result switch
    {
        ChannelResult.NotConfigured => Unavailable(),
        ChannelResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        _ => Results.Forbid()
    };
}
