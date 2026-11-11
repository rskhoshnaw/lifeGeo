using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Feedback;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Feedback from the field (plan step 5.30). Everybody signed in sends and reads their own; feedback.manage reads, answers and sets the status inside its data scope.
public static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        app.MapPost("/feedback", async (CreateFeedbackRequest request, HttpContext context, FeedbackService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, data) = await service.CreateAsync(tenantId, actorId, request, cancellationToken);
            return result == FeedbackResult.Success ? Results.Created($"/feedback/{data!.Id}", data) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateFeedback").WithTags("Feedback").Produces<FeedbackCreatedDto>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/feedback", async (bool? mine, string? status, string? kind, Guid? userId, Guid? customerId, DateTimeOffset? before, int? limit, HttpContext context, FeedbackService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, mine, status, kind, userId, customerId, before, limit, cancellationToken);
            return result == FeedbackResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListFeedback").WithTags("Feedback").Produces<List<FeedbackDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/feedback/{id:guid}", async (Guid id, HttpContext context, FeedbackService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, data) = await service.GetAsync(tenantId, actorId, id, cancellationToken);
            return result == FeedbackResult.Success ? Results.Ok(data) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetFeedback").WithTags("Feedback").Produces<FeedbackDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        // A photo is the raw body (Content-Type image/jpeg, image/png or image/webp, at most 3 MB); only the author adds photos, three at most.
        app.MapPost("/feedback/{id:guid}/photos", async (Guid id, HttpContext context, FeedbackService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            if (context.Request.ContentLength > FeedbackService.MaxPhotoBytes) return ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > FeedbackService.MaxPhotoBytes) return ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData);
                buffer.Write(chunk, 0, read);
            }
            var result = await service.AddPhotoAsync(tenantId, actorId, id, context.Request.ContentType, buffer.ToArray(), cancellationToken);
            return result == FeedbackResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("AddFeedbackPhoto").WithTags("Feedback").Accepts<byte[]>("image/jpeg", "image/png", "image/webp").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/feedback/{id:guid}/photos/{photoId:guid}", async (Guid id, Guid photoId, HttpContext context, FeedbackService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, photo) = await service.GetPhotoAsync(tenantId, actorId, id, photoId, cancellationToken);
            if (result != FeedbackResult.Success) return ToError(result);
            context.Response.Headers.CacheControl = "private, max-age=86400";   // the picture of a photo id never changes, but it is private
            return Results.File(photo!.Data, photo.ContentType);
        }).RequireAuthorization()
            .WithName("GetFeedbackPhoto").WithTags("Feedback").Produces(200, contentType: "image/jpeg")
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/feedback/{id:guid}/replies", async (Guid id, ReplyFeedbackRequest request, HttpContext context, FeedbackService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.ReplyAsync(tenantId, actorId, id, request, cancellationToken);
            return result == FeedbackResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("ReplyFeedback").WithTags("Feedback").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/feedback/{id:guid}/status", async (Guid id, SetFeedbackStatusRequest request, HttpContext context, FeedbackService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.SetStatusAsync(tenantId, actorId, id, request, cancellationToken);
            return result == FeedbackResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("SetFeedbackStatus").WithTags("Feedback").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);
    }

    private static IResult Unavailable() => ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);

    private static IResult ToError(FeedbackResult result) => result switch
    {
        FeedbackResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        FeedbackResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.FeedbackNotFound),
        FeedbackResult.TooManyPhotos => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.TooManyPhotos),
        _ => Results.Forbid()
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
}
