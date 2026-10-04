using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Customers;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Customer location and geofence radius (plan step 1.5).
public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this WebApplication app)
    {
        app.MapPut("/customers/{customerId:guid}/location", async (Guid customerId, SetCustomerLocationRequest request, HttpContext context,
            CustomerLocationService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await service.SetLocationAsync(tenantId, actorId, customerId, request, cancellationToken));
        }).RequireAuthorization()
            .WithName("SetCustomerLocation").WithTags("Customers").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/customers/{customerId:guid}/geofence", async (Guid customerId, SetGeofenceRadiusRequest request, HttpContext context,
            CustomerLocationService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await service.SetGeofenceRadiusAsync(tenantId, actorId, customerId, request, cancellationToken));
        }).RequireAuthorization()
            .WithName("SetCustomerGeofenceRadius").WithTags("Customers").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        // Customers registered outside the rep's region, waiting for approval (plan step 1.15, decision D11).
        app.MapGet("/customer-approvals", async (HttpContext context, CustomerApprovals approvals, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await approvals.ListAsync(tenantId, actorId, cancellationToken);
            return result == CustomerApprovalResult.Success ? Results.Ok(items) : ToResult(result);
        }).RequireAuthorization()
            .WithName("ListPendingCustomers").WithTags("Customers").Produces<List<CustomerDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/customers/{customerId:guid}/approval", async (Guid customerId, DecideCustomerApprovalRequest request, HttpContext context,
            CustomerApprovals approvals, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await approvals.DecideAsync(tenantId, actorId, customerId, request, cancellationToken);
            return result == CustomerApprovalResult.Success ? Results.NoContent() : ToResult(result);
        }).RequireAuthorization()
            .WithName("DecideCustomerApproval").WithTags("Customers").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);
    }

    private static IResult ToResult(CustomerApprovalResult result) => result switch
    {
        CustomerApprovalResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        CustomerApprovalResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        CustomerApprovalResult.AlreadyDecided => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.InvalidState),
        _ => ApiErrors.Problem(StatusCodes.Status403Forbidden, ApiErrors.Forbidden)
    };

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

    private static IResult ToResult(CustomerUpdateResult result) => result switch
    {
        CustomerUpdateResult.Success => Results.NoContent(),
        CustomerUpdateResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        CustomerUpdateResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        _ => Results.Forbid()
    };
}
