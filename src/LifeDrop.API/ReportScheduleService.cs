using LifeDrop.Application.Alerts;
using LifeDrop.Application.Reports;

namespace LifeDrop.API;

// Runs the scheduled reports that are due, in every active company, every minute (Reports:ScheduleIntervalSeconds, default 60; 0 turns it off).
// One company or report failing never stops the others.
public sealed class ReportScheduleService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ReportScheduleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Reports:ScheduleIntervalSeconds", 60);
        if (seconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var tenants = await scope.ServiceProvider.GetRequiredService<IAlertStore>().ListActiveTenantsAsync(stoppingToken);
                var service = scope.ServiceProvider.GetRequiredService<SavedReportService>();
                foreach (var tenant in tenants)
                {
                    try { await service.RunDueAsync(tenant, stoppingToken); }
                    catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning(e, "Scheduled reports failed for one company"); }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Scheduled reports failed");
            }
        }
    }
}
