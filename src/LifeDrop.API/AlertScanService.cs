using LifeDrop.API.Observability;
using LifeDrop.Application.Alerts;

namespace LifeDrop.API;

// Looks for new alerts and removes tracking points older than 90 days in every active company every few minutes (Alerts:ScanIntervalSeconds, default 300; 0 turns it off). The first scan comes one interval
// after the start. One company failing never stops the others.
public sealed class AlertScanService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AlertScanService> logger, JobHealth jobs, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Alerts:ScanIntervalSeconds", 300);
        if (seconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var ok = true;
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IAlertStore>();
                var service = scope.ServiceProvider.GetRequiredService<AlertService>();
                foreach (var tenant in await store.ListActiveTenantsAsync(stoppingToken))
                {
                    try
                    {
                        await service.ScanTenantAsync(tenant, stoppingToken);
                        // Old exact points go (after their summary is kept): the 90-day retention of decision D6.
                        await scope.ServiceProvider.GetRequiredService<LifeDrop.Application.Tracking.TrackingRetention>().PurgeAsync(tenant, stoppingToken);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        ok = false;
                        logger.LogWarning(e, "Alert scan failed for one company");
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ok = false;
                logger.LogWarning(e, "Alert scan failed");
            }
            jobs.Report("alert_scan", ok, TimeSpan.FromSeconds(seconds), clock.GetUtcNow());
        }
    }
}
