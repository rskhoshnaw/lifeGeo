using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace LifeDrop.API.Observability;

// Health, metrics and the request log line (plan step 3.7). The three routes are open to monitors but carry no business data: /health/live is the process,
// /health/ready adds the database (and refuses an insecure database role), /metrics needs the token in Metrics:Token and does not exist without it.
// They are left out of the OpenAPI contract and are not rate limited, so a monitor is never refused.
public static class ObservabilityEndpoints
{
    public static readonly TimeSpan SlowRequest = TimeSpan.FromSeconds(2);

    public static void UseRequestObservability(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            var failed = false;
            try { await next(); }
            catch { failed = true; throw; }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
                var status = failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
                context.RequestServices.GetRequiredService<RequestMetrics>().Record(route, context.Request.Method, status, elapsed.TotalSeconds);
                if (!route.StartsWith("/health", StringComparison.Ordinal) && route != "/metrics")
                {
                    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("LifeDrop.Requests");
                    var level = status >= 500 || elapsed >= SlowRequest ? LogLevel.Warning : LogLevel.Information;
                    // Route template and ids of the caller only: no path values, query or body reach the log.
                    logger.Log(level, "HTTP {Method} {Route} {Status} {ElapsedMs}ms tenant={Tenant} trace={TraceId}", context.Request.Method, route, status, (long)elapsed.TotalMilliseconds,
                        context.User.FindFirst("tenant_id")?.Value ?? "-", context.TraceIdentifier);
                }
            }
        });
    }

    public static void MapObservabilityEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).ExcludeFromDescription().DisableRateLimiting();

        app.MapGet("/health/ready", async (NpgsqlDataSource dataSource, DatabaseSecurityState securityState, JobHealth jobs, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var now = clock.GetUtcNow();
            var snapshot = jobs.Snapshot(now);
            var jobList = snapshot.Select(j => new { name = j.Name, stale = j.Stale, lastSuccessSecondsAgo = j.LastSuccess is null ? (long?)null : (long)(now - j.LastSuccess.Value).TotalSeconds }).ToList();
            if (securityState.IsInsecureDbRole)
                return Results.Json(new { status = "unavailable", database = "insecure_role", jobs = jobList }, statusCode: StatusCodes.Status503ServiceUnavailable);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(timeout.Token);
            }
            catch (Exception e) when (e is NpgsqlException or OperationCanceledException or InvalidOperationException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                return Results.Json(new { status = "unavailable", database = "down", jobs = jobList }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            // The database is up: a stale job means "degraded" (still 200, so the load balancer keeps the node; the monitor alerts on the text).
            return Results.Ok(new { status = snapshot.Any(j => j.Stale) ? "degraded" : "ready", database = "up", jobs = jobList });
        }).ExcludeFromDescription().DisableRateLimiting();

        app.MapGet("/metrics", (HttpContext context, IConfiguration configuration, RequestMetrics metrics, JobHealth jobs, TimeProvider clock) =>
        {
            var token = configuration["Metrics:Token"];
            if (string.IsNullOrEmpty(token)) return Results.NotFound();
            var header = context.Request.Headers.Authorization.ToString();
            var given = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(token))))
                return Results.Unauthorized();
            return Results.Text(metrics.Render(jobs, clock.GetUtcNow()), "text/plain; version=0.0.4; charset=utf-8");
        }).ExcludeFromDescription().DisableRateLimiting();
    }
}
