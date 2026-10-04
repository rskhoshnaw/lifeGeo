using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using LifeDrop.Application.Auth;
using LifeDrop.Application.Devices;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Roles;
using LifeDrop.Infrastructure.Auth;
using LifeDrop.Infrastructure.Devices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace LifeDrop.API;

public class DatabaseSecurityState
{
    public bool IsInsecureDbRole { get; set; }
}

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (builder.Environment.IsDevelopment())
        {
            builder.Configuration.AddJsonFile(
                Path.Combine(builder.Environment.ContentRootPath, "appsettings.Development.local.json"),
                optional: true, reloadOnChange: false);
            builder.Configuration.AddEnvironmentVariables();
            if (args.Length > 0) builder.Configuration.AddCommandLine(args);
        }
        var connectionString = builder.Configuration.GetConnectionString("LifeDrop")
            ?? throw new InvalidOperationException("ConnectionStrings:LifeDrop is required.");
        var issuer = builder.Configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException("Jwt:Issuer is required.");
        var audience = builder.Configuration["Jwt:Audience"]
            ?? throw new InvalidOperationException("Jwt:Audience is required.");
        var signingKey = builder.Configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is required.");
        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");

        builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        builder.Services.AddSingleton<DatabaseSecurityState>();
        builder.Services.AddScoped<IRoleAssignmentStore, PostgresRoleAssignmentStore>();
        builder.Services.AddScoped<AssignRole>();
        builder.Services.AddScoped<ILoginStore, PostgresLoginStore>();
        builder.Services.AddScoped<Login>();
        builder.Services.AddScoped<ISessionStore, PostgresSessionStore>();
        builder.Services.AddScoped<IDeviceAccessStore, PostgresDeviceAccessStore>();
        
        // Customers
        builder.Services.AddScoped<LifeDrop.Application.Roles.IPermissionStore, LifeDrop.Infrastructure.Roles.PostgresPermissionStore>();
        builder.Services.AddScoped<LifeDrop.Application.Customers.ICustomerStore, LifeDrop.Infrastructure.Customers.PostgresCustomerStore>();
        builder.Services.AddScoped<LifeDrop.Application.Customers.CreateCustomer>();
        builder.Services.AddScoped<LifeDrop.Application.Customers.GetCustomer>();
        builder.Services.AddScoped<LifeDrop.Application.Customers.ListCustomers>();
        
        // Visits
        builder.Services.AddScoped<LifeDrop.Application.Visits.IVisitStore, LifeDrop.Infrastructure.Visits.PostgresVisitStore>();
        builder.Services.AddScoped<LifeDrop.Application.Visits.CreateVisit>();
        builder.Services.AddScoped<LifeDrop.Application.Visits.ListAssignedVisits>();
        builder.Services.AddScoped<LifeDrop.Application.Visits.MarkVisitEnRoute>();
        builder.Services.AddScoped<LifeDrop.Application.Visits.MarkVisitArrived>();
        builder.Services.AddScoped<LifeDrop.Application.Visits.MarkVisitCompleted>();
        builder.Services.AddScoped<LifeDrop.Application.Visits.MarkVisitFailed>();
        builder.Services.AddScoped<LifeDrop.Application.Regions.IRegionTeamStore, LifeDrop.Infrastructure.Regions.PostgresRegionTeamStore>();
        builder.Services.AddScoped<LifeDrop.Application.Regions.RegionTeamService>();
        builder.Services.AddScoped<KillUserSessions>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<LifeDrop.Application.Visits.CreateUnplannedVisit>();
        builder.Services.AddScoped<LifeDrop.Application.Visits.ManageVisit>();
        builder.Services.AddScoped<LifeDrop.Application.Visits.VisitReports>();
        builder.Services.AddScoped<LifeDrop.Application.Visits.CheckInVisit>();
        builder.Services.AddScoped<LifeDrop.Application.Customers.CustomerLocationService>();
        builder.Services.AddScoped<LifeDrop.Application.Audit.IAuditLogStore, LifeDrop.Infrastructure.Audit.PostgresAuditLogStore>();
        builder.Services.AddScoped<LifeDrop.Application.Audit.SearchAuditLogs>();

        builder.Services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        });
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var subject = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                        var tenant = context.Principal?.FindFirst("tenant_id")?.Value;
                        var family = context.Principal?.FindFirst("session_family")?.Value;
                        var device = context.Principal?.FindFirst("device_id")?.Value;
                        if (!Guid.TryParse(subject, out var userId) ||
                            !Guid.TryParse(tenant, out var tenantId) ||
                            !Guid.TryParse(family, out var familyId) ||
                            (device is not null && !Guid.TryParse(device, out _)))
                        {
                            context.Fail("Session claims are invalid.");
                            return;
                        }
                        var identity = new SessionIdentity(new TenantId(tenantId),
                            new UserId(userId), familyId,
                            device is null ? null : Guid.Parse(device));
                        var store = context.HttpContext.RequestServices.GetRequiredService<ISessionStore>();
                        if (!await store.IsAccessActiveAsync(identity,
                            context.HttpContext.RequestAborted))
                            context.Fail("Session is inactive.");
                    }
                };
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiErrors.Customize);
        // Malformed route/query/body values must be 400 in every environment, never an exception (500).
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

        var app = builder.Build();
        (string AccessToken, DateTime ExpiresAt) CreateAccessToken(SessionIdentity identity)
        {
            var expiresAt = DateTime.UtcNow.AddMinutes(15);
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, identity.UserId.ToString()),
                new("tenant_id", identity.TenantId.ToString()),
                new("session_family", identity.FamilyId.ToString()),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };
            if (identity.DeviceId.HasValue)
                claims.Add(new Claim("device_id", identity.DeviceId.Value.ToString()));
            var token = new JwtSecurityToken(issuer, audience, claims,
                notBefore: DateTime.UtcNow, expires: expiresAt,
                signingCredentials: new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    SecurityAlgorithms.HmacSha256));
            return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
        }
        static SessionIdentity? ReadSessionIdentity(HttpContext context)
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;
            var family = context.User.FindFirst("session_family")?.Value;
            var device = context.User.FindFirst("device_id")?.Value;
            if (!Guid.TryParse(subject, out var userId) ||
                !Guid.TryParse(tenant, out var tenantId) ||
                !Guid.TryParse(family, out var familyId) ||
                (device is not null && !Guid.TryParse(device, out _)))
                return null;
            return new SessionIdentity(new TenantId(tenantId), new UserId(userId),
                familyId, device is null ? null : Guid.Parse(device));
        }
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        if (app.Environment.IsDevelopment()) app.MapOpenApi();
        if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/", () => Results.Ok(new
        {
            Service = "LifeDrop API",
            Status = "running",
            OpenApi = "/openapi/v1.json"
        }))
            .WithName("GetServiceStatus").WithTags("Status").Produces(200).ProducesProblem(429);

        app.MapPost("/auth/login", async (LoginRequest request, Login login,
            IDeviceAccessStore devices, ISessionStore sessions, HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!app.Environment.IsDevelopment() && !context.Request.IsHttps)
                return Results.BadRequest();
            LoginIdentity? identity;
            try { identity = await login.ExecuteAsync(request, cancellationToken); }
            catch (ArgumentException) { return Results.BadRequest(); }
            if (identity is null) return Results.Unauthorized();
            Guid? deviceId = null;
            if (identity.RequiresDeviceBinding || request.DeviceId.HasValue)
            {
                if (!request.DeviceId.HasValue || request.DeviceId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(request.DeviceSecret) ||
                    !await devices.IsApprovedAsync(identity.TenantId, identity.UserId,
                        request.DeviceId.Value, request.DeviceSecret, cancellationToken))
                {
                    await devices.RecordDeniedAsync(identity.TenantId, identity.UserId,
                        cancellationToken);
                    return ApiErrors.Problem(StatusCodes.Status403Forbidden, ApiErrors.DeviceNotApproved);
                }
                deviceId = request.DeviceId;
            }
            var session = await sessions.IssueAsync(identity, deviceId, cancellationToken);
            var (accessToken, expiresAt) = CreateAccessToken(session.Identity);
            return Results.Ok(new TokenResponse(accessToken, session.RefreshToken, "Bearer",
                expiresAt, session.RefreshExpiresAt));
        }).RequireRateLimiting("login")
            .WithName("Login").WithTags("Auth").Produces<TokenResponse>(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);

        app.MapPost("/auth/refresh", async (RefreshRequest request, ISessionStore sessions,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!app.Environment.IsDevelopment() && !context.Request.IsHttps)
                return Results.BadRequest();
            var rotated = await sessions.RotateAsync(request, cancellationToken);
            if (rotated is null) return Results.Unauthorized();
            var (accessToken, expiresAt) = CreateAccessToken(rotated.Identity);
            return Results.Ok(new TokenResponse(accessToken, rotated.RefreshToken, "Bearer",
                expiresAt, rotated.RefreshExpiresAt));
        }).RequireRateLimiting("login")
            .WithName("RefreshSession").WithTags("Auth").Produces<TokenResponse>(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(429);

        app.MapPost("/auth/logout", async (HttpContext context, ISessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            var identity = ReadSessionIdentity(context);
            if (identity is null) return Results.Forbid();
            await sessions.RevokeFamilyAsync(identity, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization()
            .WithName("Logout").WithTags("Auth").Produces(204).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);

        app.MapPost("/auth/devices/register", async (RegisterDeviceRequest request,
            Login login, IDeviceAccessStore devices, HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!app.Environment.IsDevelopment() && !context.Request.IsHttps)
                return Results.BadRequest();
            if (string.IsNullOrWhiteSpace(request.DeviceLabel) || request.DeviceLabel.Length > 100 ||
                (request.Platform != "android" && request.Platform != "ios") ||
                string.IsNullOrWhiteSpace(request.InstallId) || request.InstallId.Length > 200 ||
                string.IsNullOrWhiteSpace(request.AppVersion) || request.AppVersion.Length > 50)
                return Results.BadRequest();
            LoginIdentity? identity;
            try
            {
                identity = await login.ExecuteAsync(new LoginRequest(request.TenantCode,
                    request.Email, request.Password), cancellationToken);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            if (identity is null) return Results.Unauthorized();
            var device = await devices.RegisterAsync(identity.TenantId, identity.UserId,
                request, cancellationToken);
            return device is null ? Results.Conflict() : Results.Ok(device);
        }).RequireRateLimiting("login")
            .WithName("RegisterDevice").WithTags("Devices").Produces<LifeDrop.Application.Devices.RegisteredDevice>(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(409).ProducesProblem(429);

        app.MapPost("/devices/{deviceId:guid}/approve", async (Guid deviceId,
            HttpContext context, IDeviceAccessStore devices,
            CancellationToken cancellationToken) =>
        {
            var identity = ReadSessionIdentity(context);
            if (identity is null) return Results.Forbid();
            var result = await devices.ApproveAsync(identity.TenantId, identity.UserId,
                deviceId, cancellationToken);
            return result switch
            {
                DeviceChangeResult.Changed => Results.NoContent(),
                DeviceChangeResult.Forbidden => Results.Forbid(),
                _ => Results.NotFound()
            };
        }).RequireAuthorization()
            .WithName("ApproveDevice").WithTags("Devices").Produces(204).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);

        app.MapPost("/devices/{deviceId:guid}/block", async (Guid deviceId,
            HttpContext context, IDeviceAccessStore devices,
            CancellationToken cancellationToken) =>
        {
            var identity = ReadSessionIdentity(context);
            if (identity is null) return Results.Forbid();
            var result = await devices.BlockAsync(identity.TenantId, identity.UserId,
                deviceId, cancellationToken);
            return result switch
            {
                DeviceChangeResult.Changed => Results.NoContent(),
                DeviceChangeResult.Forbidden => Results.Forbid(),
                _ => Results.NotFound()
            };
        }).RequireAuthorization()
            .WithName("BlockDevice").WithTags("Devices").Produces(204).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);

        app.MapPost("/users/{userId:guid}/roles/{roleId:guid}", async (
            Guid userId, Guid roleId, HttpContext context, AssignRole assignRole,
            CancellationToken cancellationToken) =>
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;
            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();
            if (userId == Guid.Empty || roleId == Guid.Empty)
                return Results.BadRequest();

            var result = await assignRole.ExecuteAsync(
                new RoleAssignmentActor(new TenantId(tenantId), new UserId(actorId)),
                new UserId(userId), new RoleId(roleId), cancellationToken);
            return result switch
            {
                AssignRoleResult.Assigned => Results.NoContent(),
                AssignRoleResult.AlreadyAssigned => Results.Conflict(),
                AssignRoleResult.Forbidden => Results.Forbid(),
                AssignRoleResult.UserNotFound or AssignRoleResult.RoleNotFound => Results.NotFound(),
                _ => ApiErrors.Problem(StatusCodes.Status500InternalServerError, ApiErrors.Unexpected)
            };
        }).RequireAuthorization()
            .WithName("AssignUserRole").WithTags("Users").Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);

        app.MapPost("/customers", async (
            LifeDrop.Application.Customers.CreateCustomerRequest request, 
            HttpContext context, 
            LifeDrop.Application.Customers.CreateCustomer createCustomer,
            LifeDrop.Application.Roles.IPermissionStore permissionStore,
            DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Results.StatusCode(503);

            var subject = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;

            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            if (!await permissionStore.HasPermissionAsync(new LifeDrop.Domain.Common.TenantId(tenantId), new LifeDrop.Domain.Common.UserId(actorId), "customers.write", cancellationToken))
                return Results.Forbid();

            var result = await createCustomer.ExecuteAsync(new LifeDrop.Domain.Common.TenantId(tenantId), new LifeDrop.Domain.Common.UserId(actorId), request, cancellationToken);
            return result.Result switch
            {
                LifeDrop.Application.Customers.CreateCustomerResult.Created => Results.Created($"/customers/{result.Id.Value}", result.Id.Value),
                LifeDrop.Application.Customers.CreateCustomerResult.DuplicateCode => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.DuplicateCode),
                LifeDrop.Application.Customers.CreateCustomerResult.Forbidden => Results.Forbid(),
                LifeDrop.Application.Customers.CreateCustomerResult.InvalidData => Results.BadRequest(),
                _ => ApiErrors.Problem(StatusCodes.Status500InternalServerError, ApiErrors.Unexpected)
            };}).RequireAuthorization()
            .WithName("CreateCustomer").WithTags("Customers").Produces<Guid>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/visits", async (
            LifeDrop.Application.Visits.CreateVisitRequest request, 
            HttpContext context, 
            LifeDrop.Application.Visits.CreateVisit createVisit,
            LifeDrop.Application.Roles.IPermissionStore permissionStore,
            DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Results.StatusCode(503);

            var subject = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;

            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            var result = await createVisit.ExecuteAsync(new LifeDrop.Domain.Common.TenantId(tenantId), new LifeDrop.Domain.Common.UserId(actorId), request, cancellationToken);
            return result.Result switch
            {
                LifeDrop.Application.Visits.CreateVisitResult.Created => Results.Created($"/visits/{result.VisitId}", new CreatedResponse(result.VisitId!.Value)),
                LifeDrop.Application.Visits.CreateVisitResult.Forbidden => Results.Forbid(),
                LifeDrop.Application.Visits.CreateVisitResult.UserNotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.UserNotFound),
                LifeDrop.Application.Visits.CreateVisitResult.CustomerNotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.CustomerNotFound),
                LifeDrop.Application.Visits.CreateVisitResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
                _ => ApiErrors.Problem(StatusCodes.Status500InternalServerError, ApiErrors.Unexpected)
            };}).RequireAuthorization()
            .WithName("CreateVisit").WithTags("Visits").Produces<CreatedResponse>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/visits", async (
            DateOnly? scheduleDate,
            HttpContext context, 
            LifeDrop.Application.Visits.ListAssignedVisits listVisits,
            DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Results.StatusCode(503);

            var subject = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;

            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            // Employee only sees their own visit schedule - no special permission required since they just query by their own actorId
            var result = await listVisits.ExecuteAsync(new LifeDrop.Domain.Common.TenantId(tenantId), new LifeDrop.Domain.Common.UserId(actorId), scheduleDate, cancellationToken);
            return result is null ? Results.BadRequest() : Results.Ok(result);
        }).RequireAuthorization()
            .WithName("ListAssignedVisits").WithTags("Visits").Produces<List<LifeDrop.Application.Visits.VisitDto>>(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/visits/{visitId:guid}/en-route", async (
            Guid visitId,
            HttpContext context, 
            LifeDrop.Application.Visits.MarkVisitEnRoute markEnRoute,
            DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Results.StatusCode(503);

            var subject = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;

            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            var result = await markEnRoute.ExecuteAsync(new LifeDrop.Domain.Common.TenantId(tenantId), new LifeDrop.Domain.Common.UserId(actorId), visitId, cancellationToken);
            return result switch
            {
                LifeDrop.Application.Visits.MarkVisitResult.Success => Results.NoContent(),
                LifeDrop.Application.Visits.MarkVisitResult.NotFound => Results.NotFound(),
                LifeDrop.Application.Visits.MarkVisitResult.Forbidden => Results.Forbid(),
                LifeDrop.Application.Visits.MarkVisitResult.InvalidState => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidState),
                LifeDrop.Application.Visits.MarkVisitResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.ConcurrencyConflict),
                _ => ApiErrors.Problem(StatusCodes.Status500InternalServerError, ApiErrors.Unexpected)
            };
        }).RequireAuthorization()
            .WithName("MarkVisitEnRoute").WithTags("Visits").Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/visits/{visitId:guid}/arrive", async (
            Guid visitId,
            HttpContext context, 
            LifeDrop.Application.Visits.MarkVisitArrived markArrived,
            DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Results.StatusCode(503);

            var subject = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;

            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            var result = await markArrived.ExecuteAsync(new LifeDrop.Domain.Common.TenantId(tenantId), new LifeDrop.Domain.Common.UserId(actorId), visitId, cancellationToken);
            return result switch
            {
                LifeDrop.Application.Visits.MarkVisitResult.Success => Results.NoContent(),
                LifeDrop.Application.Visits.MarkVisitResult.NotFound => Results.NotFound(),
                LifeDrop.Application.Visits.MarkVisitResult.Forbidden => Results.Forbid(),
                LifeDrop.Application.Visits.MarkVisitResult.InvalidState => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidState),
                LifeDrop.Application.Visits.MarkVisitResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.ConcurrencyConflict),
                _ => ApiErrors.Problem(StatusCodes.Status500InternalServerError, ApiErrors.Unexpected)
            };
        }).RequireAuthorization()
            .WithName("MarkVisitArrived").WithTags("Visits").Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/visits/{visitId:guid}/complete", async (
            Guid visitId,
            HttpContext context, 
            LifeDrop.Application.Visits.MarkVisitCompleted markCompleted,
            DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Results.StatusCode(503);

            var subject = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;

            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            var result = await markCompleted.ExecuteAsync(new LifeDrop.Domain.Common.TenantId(tenantId), new LifeDrop.Domain.Common.UserId(actorId), visitId, cancellationToken);
            return result switch
            {
                LifeDrop.Application.Visits.MarkVisitResult.Success => Results.NoContent(),
                LifeDrop.Application.Visits.MarkVisitResult.NotFound => Results.NotFound(),
                LifeDrop.Application.Visits.MarkVisitResult.Forbidden => Results.Forbid(),
                LifeDrop.Application.Visits.MarkVisitResult.InvalidState => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidState),
                LifeDrop.Application.Visits.MarkVisitResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.ConcurrencyConflict),
                _ => ApiErrors.Problem(StatusCodes.Status500InternalServerError, ApiErrors.Unexpected)
            };
        }).RequireAuthorization()
            .WithName("CompleteVisit").WithTags("Visits").Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/visits/{visitId:guid}/fail", async (
            Guid visitId,
            [Microsoft.AspNetCore.Mvc.FromBody] FailVisitRequest request,
            HttpContext context, 
            LifeDrop.Application.Visits.MarkVisitFailed markFailed,
            DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Results.StatusCode(503);

            var subject = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;

            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            var result = await markFailed.ExecuteAsync(new LifeDrop.Domain.Common.TenantId(tenantId), new LifeDrop.Domain.Common.UserId(actorId), visitId, request.Reason, cancellationToken);
            return result switch
            {
                LifeDrop.Application.Visits.MarkVisitResult.Success => Results.NoContent(),
                LifeDrop.Application.Visits.MarkVisitResult.NotFound => Results.NotFound(),
                LifeDrop.Application.Visits.MarkVisitResult.Forbidden => Results.Forbid(),
                LifeDrop.Application.Visits.MarkVisitResult.InvalidState => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidState),
                LifeDrop.Application.Visits.MarkVisitResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
                LifeDrop.Application.Visits.MarkVisitResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.ConcurrencyConflict),
                _ => ApiErrors.Problem(StatusCodes.Status500InternalServerError, ApiErrors.Unexpected)
            };
        }).RequireAuthorization()
            .WithName("FailVisit").WithTags("Visits").Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/customers", async (
            int? limit,
            DateTimeOffset? cursorCreatedAt,
            Guid? cursorId,
            HttpContext context, 
            LifeDrop.Application.Customers.ListCustomers listCustomers,
            LifeDrop.Application.Roles.IPermissionStore permissionStore,
            DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Results.StatusCode(503);

            var subject = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;

            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            var readScope = await permissionStore.GetAccessScopeAsync(new LifeDrop.Domain.Common.TenantId(tenantId), new LifeDrop.Domain.Common.UserId(actorId), "customers.read", cancellationToken);
            if (readScope is null) return Results.Forbid();

            var result = await listCustomers.ExecuteAsync(new LifeDrop.Domain.Common.TenantId(tenantId), readScope, limit, cursorCreatedAt, cursorId, cancellationToken);
            return result.IsValid ? Results.Ok(result.Items) : Results.BadRequest();
        }).RequireAuthorization()
            .WithName("ListCustomers").WithTags("Customers").Produces<List<LifeDrop.Application.Customers.CustomerDto>>(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/customers/{id:guid}", async (
            Guid id, 
            HttpContext context, 
            LifeDrop.Application.Customers.GetCustomer getCustomer,
            LifeDrop.Application.Roles.IPermissionStore permissionStore,
            DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Results.StatusCode(503);

            var subject = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;

            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            var readScope = await permissionStore.GetAccessScopeAsync(new LifeDrop.Domain.Common.TenantId(tenantId), new LifeDrop.Domain.Common.UserId(actorId), "customers.read", cancellationToken);
            if (readScope is null) return Results.Forbid();

            var result = await getCustomer.ExecuteAsync(new LifeDrop.Domain.Common.TenantId(tenantId), new LifeDrop.Domain.Common.CustomerId(id), readScope, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);}).RequireAuthorization()
            .WithName("GetCustomer").WithTags("Customers").Produces<LifeDrop.Application.Customers.CustomerDto>(200).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        using (var scope = app.Services.CreateScope())
        {
            var dataSource = scope.ServiceProvider.GetRequiredService<Npgsql.NpgsqlDataSource>();
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new Npgsql.NpgsqlCommand("SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user", connection);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var roleName = reader.GetString(0);
                var isSuper = reader.GetBoolean(1);
                var isBypass = reader.GetBoolean(2);
                var securityState = scope.ServiceProvider.GetRequiredService<DatabaseSecurityState>();
                if (isSuper || isBypass)
                {
                    securityState.IsInsecureDbRole = true;
                    app.Logger.LogCritical($"BLOCKER: API database role '{roleName}' is superuser or has BYPASSRLS. RLS will not work! Required: A non-superuser role without BYPASSRLS.");
                }
                else
                {
                    app.Logger.LogInformation($"API database role '{roleName}' has correct privileges (no BYPASSRLS/SUPERUSER).");
                }
            }
        }

        LifeDrop.API.Endpoints.RegionTeamEndpoints.MapRegionTeamEndpoints(app);
        LifeDrop.API.Endpoints.SessionEndpoints.MapSessionEndpoints(app);
        LifeDrop.API.Endpoints.AuditEndpoints.MapAuditEndpoints(app);
        LifeDrop.API.Endpoints.VisitEndpoints.MapVisitEndpoints(app);
        LifeDrop.API.Endpoints.CustomerEndpoints.MapCustomerEndpoints(app);

        app.Run();
    }
}






public record FailVisitRequest(string Reason);
public record TokenResponse(string AccessToken, string RefreshToken, string TokenType, DateTime ExpiresAt, DateTime RefreshExpiresAt);
public record CreatedResponse(Guid Id);


