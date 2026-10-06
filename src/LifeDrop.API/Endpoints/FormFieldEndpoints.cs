using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Forms;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Custom visit-form fields (plan step 3.2, part 2): definitions by managers (forms.manage), answers by the assigned user while the visit is InVisit.
public static class FormFieldEndpoints
{
    public static void MapFormFieldEndpoints(this WebApplication app)
    {
        app.MapGet("/form-fields", async (string? visitType, bool? includeInactive, HttpContext context, FormFieldService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, visitType, includeInactive == true, cancellationToken);
            return result == FormResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListFormFields").WithTags("Forms").Produces<List<FormFieldDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/form-fields", async (CreateFormFieldRequest request, HttpContext context, FormFieldService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateAsync(tenantId, actorId, request, cancellationToken);
            return result == FormResult.Success ? Results.Created($"/form-fields/{id}", new CreatedResponse(id!.Value)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateFormField").WithTags("Forms").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/form-fields/{fieldId:guid}", async (Guid fieldId, UpdateFormFieldRequest request, HttpContext context, FormFieldService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.UpdateAsync(tenantId, actorId, fieldId, request, cancellationToken);
            return result == FormResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("UpdateFormField").WithTags("Forms").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/visits/{visitId:guid}/report/custom", async (Guid visitId, SaveCustomAnswersRequest request, HttpContext context, FormFieldService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.SaveAnswersAsync(tenantId, actorId, visitId, request, cancellationToken);
            return result == FormResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("SaveVisitCustomAnswers").WithTags("Forms").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/visits/{visitId:guid}/report/custom", async (Guid visitId, HttpContext context, FormFieldService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, answers) = await service.GetAnswersAsync(tenantId, actorId, visitId, cancellationToken);
            return result == FormResult.Success ? Results.Ok(answers) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetVisitCustomAnswers").WithTags("Forms").Produces<CustomAnswersDto>(200)
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

    private static IResult ToError(FormResult result) => result switch
    {
        FormResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        FormResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        FormResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        _ => Results.Forbid()
    };
}
