using LifeDrop.API.Observability;
using LifeDrop.Application.Alerts;
using LifeDrop.Application.Push;

namespace LifeDrop.API;

// Sends the waiting push messages of every active company every few seconds (Push:DispatchIntervalSeconds, default 30; 0 turns
// it off). Does nothing until a Firebase key is configured (docs/PUSH.md). One company failing never stops the others.
public sealed class PushDispatchService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<PushDispatchService> logger, JobHealth jobs, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Push:DispatchIntervalSeconds", 30);
        if (seconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var ok = true;
            var ran = false;
            try
            {
                using var scope = scopes.CreateScope();
                if (!scope.ServiceProvider.GetRequiredService<IPushSender>().IsConfigured) continue;   // no key: nothing to watch
                ran = true;
                var tenants = await scope.ServiceProvider.GetRequiredService<IAlertStore>().ListActiveTenantsAsync(stoppingToken);
                var dispatcher = scope.ServiceProvider.GetRequiredService<PushDispatcher>();
                foreach (var tenant in tenants)
                {
                    try { await dispatcher.DispatchTenantAsync(tenant, stoppingToken); }
                    catch (Exception e) when (e is not OperationCanceledException) { ok = false; logger.LogWarning(e, "Push dispatch failed for one company"); }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ok = false;
                ran = true;
                logger.LogWarning(e, "Push dispatch failed");
            }
            if (ran) jobs.Report("push_dispatch", ok, TimeSpan.FromSeconds(seconds), clock.GetUtcNow());
        }
    }
}
