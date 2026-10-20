using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Users;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Routes for the web panel's user, device and role screens (plan step 1.17; adding an account, step 4.9).
public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        app.MapGet("/me", async (HttpContext context, UserDirectory directory, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var me = await directory.GetCurrentAsync(tenantId, actorId, cancellationToken);
            return me is null ? Results.Forbid() : Results.Ok(me);
        }).RequireAuthorization()
            .WithName("GetCurrentUser").WithTags("Users").Produces<CurrentUserDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/users", async (bool? includeInactive, HttpContext context, UserDirectory directory, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await directory.ListUsersAsync(tenantId, actorId, includeInactive == true, cancellationToken);
            return ToResult(result, items);
        }).RequireAuthorization()
            .WithName("ListUsers").WithTags("Users").Produces<List<UserSummaryDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/users", async (CreateUserRequest request, HttpContext context, CreateStaffUser create, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, created) = await create.ExecuteAsync(tenantId, actorId, request, cancellationToken);
            return result switch
            {
                CreateUserResult.Created => Results.Created($"/users/{created!.Id}", created),
                CreateUserResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
                CreateUserResult.EmailTaken => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
                CreateUserResult.RoleNotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
                _ => Results.Forbid()
            };
        }).RequireAuthorization()
            .WithName("CreateUser").WithTags("Users").Produces<CreatedUserDto>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        // Plan step 4.46: a signed-in user changes their own password (the current one is needed); their other sessions end, this one stays. Rate limited like the login.
        app.MapPut("/me/password", async (ChangeOwnPasswordRequest request, HttpContext context, ManageUsers manage,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            if (!Guid.TryParse(context.User.FindFirst("session_family")?.Value, out var family)) return Results.Forbid();
            var result = await manage.ChangeOwnPasswordAsync(tenantId, actorId, family, request, cancellationToken);
            return result == ManageUserResult.WrongPassword ? ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.WrongPassword) : ToResult(result);
        }).RequireAuthorization().RequireRateLimiting("login")
            .WithName("ChangeOwnPassword").WithTags("Users").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/users/{id:guid}/email", async (Guid id, ChangeUserEmailRequest request, HttpContext context, ManageUsers manage,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await manage.ChangeEmailAsync(tenantId, actorId, new UserId(id), request, cancellationToken));
        }).RequireAuthorization()
            .WithName("ChangeUserEmail").WithTags("Users").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/users/{id:guid}/password", async (Guid id, SetUserPasswordRequest request, HttpContext context, ManageUsers manage,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await manage.SetPasswordAsync(tenantId, actorId, new UserId(id), request, cancellationToken));
        }).RequireAuthorization()
            .WithName("SetUserPassword").WithTags("Users").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/users/{id:guid}/city", async (Guid id, SetUserCityRequest request, HttpContext context, ManageUsers manage,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await manage.SetCityAsync(tenantId, actorId, new UserId(id), request, cancellationToken));
        }).RequireAuthorization()
            .WithName("SetUserCity").WithTags("Users").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapDelete("/users/{id:guid}/roles/{roleId:guid}", async (Guid id, Guid roleId, HttpContext context, ManageUsers manage,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await manage.RemoveRoleAsync(tenantId, actorId, new UserId(id), roleId, cancellationToken));
        }).RequireAuthorization()
            .WithName("RemoveUserRole").WithTags("Users").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/users/{id:guid}/deactivate", async (Guid id, HttpContext context, ManageUsers manage,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await manage.SetActiveAsync(tenantId, actorId, new UserId(id), false, cancellationToken));
        }).RequireAuthorization()
            .WithName("DeactivateUser").WithTags("Users").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/users/{id:guid}/activate", async (Guid id, HttpContext context, ManageUsers manage,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await manage.SetActiveAsync(tenantId, actorId, new UserId(id), true, cancellationToken));
        }).RequireAuthorization()
            .WithName("ActivateUser").WithTags("Users").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/roles", async (HttpContext context, UserDirectory directory, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await directory.ListRolesAsync(tenantId, actorId, cancellationToken);
            return ToResult(result, items);
        }).RequireAuthorization()
            .WithName("ListRoles").WithTags("Users").Produces<List<RoleDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/devices", async (string? status, string? platform, string? q, HttpContext context, UserDirectory directory, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await directory.ListDevicesAsync(tenantId, actorId, status, platform, q, cancellationToken);
            return ToResult(result, items);
        }).RequireAuthorization()
            .WithName("ListDevices").WithTags("Devices").Produces<List<DeviceSummaryDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
    }

    private static IResult ToResult(ManageUserResult result) => result switch
    {
        ManageUserResult.Done => Results.NoContent(),
        ManageUserResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        ManageUserResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        ManageUserResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        _ => Results.Forbid()
    };

    private static IResult ToResult<T>(DirectoryResult result, IReadOnlyList<T> items) => result switch
    {
        DirectoryResult.Success => Results.Ok(items),
        DirectoryResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        _ => Results.Forbid()
    };

    private static IResult Unavailable() => ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);

    private static bool TryReadActor(HttpContext context, out TenantId tenantId, out UserId actorId)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var tenant = context.User.FindFirst("tenant_id")?.Value;
        var ok = Guid.TryParse(subject, out var actor) && actor != Guid.Empty && Guid.TryParse(tenant, out var t) && t != Guid.Empty;
        tenantId = ok ? new TenantId(Guid.Parse(tenant!)) : default;
        actorId = ok ? new UserId(actor) : default;
        return ok;
    }
}
