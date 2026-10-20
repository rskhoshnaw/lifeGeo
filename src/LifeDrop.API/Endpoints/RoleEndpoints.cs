using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Roles built in the panel (plan step 4.38): the list of permissions that exist, and creating, changing and deleting roles. Listing roles stays in UserEndpoints (GET /roles).
public static class RoleEndpoints
{
    public static void MapRoleEndpoints(this WebApplication app)
    {
        app.MapGet("/permissions", async (HttpContext context, RoleManagement service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListPermissionsAsync(tenantId, actorId, cancellationToken);
            return result == RoleResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListPermissions").WithTags("Roles").Produces<List<PermissionDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/roles", async (SaveRoleRequest request, HttpContext context, RoleManagement service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateAsync(tenantId, actorId, request, cancellationToken);
            return result == RoleResult.Success ? Results.Created($"/roles/{id}", new CreatedResponse(id!.Value)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateRole").WithTags("Roles").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/roles/{roleId:guid}", async (Guid roleId, SaveRoleRequest request, HttpContext context, RoleManagement service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.UpdateAsync(tenantId, actorId, roleId, request, cancellationToken);
            return result == RoleResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("UpdateRole").WithTags("Roles").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapDelete("/roles/{roleId:guid}", async (Guid roleId, HttpContext context, RoleManagement service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.DeleteAsync(tenantId, actorId, roleId, cancellationToken);
            return result == RoleResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("DeleteRole").WithTags("Roles").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);
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

    private static IResult ToError(RoleResult result) => result switch
    {
        RoleResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        RoleResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        RoleResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        _ => Results.Forbid()
    };
}
