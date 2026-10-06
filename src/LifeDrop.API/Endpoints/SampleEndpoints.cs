using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Samples;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Pharma samples (plan step 2.6): recorded for a customer (and optionally the visit); a manager voids a record with a reason.
public static class SampleEndpoints
{
    public static void MapSampleEndpoints(this WebApplication app)
    {
        app.MapPost("/samples", async (RecordSampleRequest request, HttpContext context, SampleService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, sample) = await service.RecordAsync(tenantId, actorId, request, cancellationToken);
            if (result != SampleResult.Success) return ToError(result);
            return sample!.Replayed ? Results.Ok(sample) : Results.Created($"/samples/{sample.Id}", sample);
        }).RequireAuthorization()
            .WithName("RecordSample").WithTags("Samples").Produces<CreatedSampleDto>(201).Produces<CreatedSampleDto>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/samples", async (Guid? customerId, Guid? productId, int? limit, DateTimeOffset? before, Guid? beforeId, HttpContext context, SampleService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, customerId, productId, limit, before, beforeId, cancellationToken);
            return result == SampleResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListSamples").WithTags("Samples").Produces<List<SampleDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/samples/{sampleId:guid}", async (Guid sampleId, HttpContext context, SampleService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, sample) = await service.GetAsync(tenantId, actorId, sampleId, cancellationToken);
            return result == SampleResult.Success ? Results.Ok(sample) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetSample").WithTags("Samples").Produces<SampleDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/samples/{sampleId:guid}/void", async (Guid sampleId, VoidSampleRequest request, HttpContext context, SampleService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.VoidAsync(tenantId, actorId, sampleId, request, cancellationToken);
            return result == SampleResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("VoidSample").WithTags("Samples").Produces(204)
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

    private static IResult ToError(SampleResult result) => result switch
    {
        SampleResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        SampleResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        SampleResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        SampleResult.GpsMocked => ApiErrors.Problem(StatusCodes.Status422UnprocessableEntity, ApiErrors.GpsMocked),
        _ => Results.Forbid()
    };
}
