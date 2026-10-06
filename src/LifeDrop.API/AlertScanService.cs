using LifeDrop.Application.Alerts;

namespace LifeDrop.API;

// Looks for new alerts in every active company every few minutes (Alerts:ScanIntervalSeconds, default 300; 0 turns it off). The first scan comes one interval
// after the start. One company failing never stops the others.
public sealed class AlertScanService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AlertScanService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Alerts:ScanIntervalSeconds", 300);
        if (seconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
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
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        logger.LogWarning(e, "Alert scan failed for one company");
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Alert scan failed");
            }
        }
    }
}
