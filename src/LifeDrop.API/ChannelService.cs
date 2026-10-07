using LifeDrop.API.Observability;
using LifeDrop.Application.Alerts;
using LifeDrop.Application.Channels;

namespace LifeDrop.API;

// The message channels (plan step 3.4, D17), OFF by default: every Channels:IntervalSeconds (default 15; 0 turns it off) it reads what the company bot received (the
// one-time codes of users who link their Telegram chat) and sends the waiting messages of every active company. It does nothing until Channels:Enabled is true and a
// bot token is set (docs/CHANNELS.md). One company failing never stops the others.
public sealed class ChannelService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ChannelService> logger, JobHealth jobs, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Channels:IntervalSeconds", 15);
        if (seconds <= 0) return;
        long offset = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var ok = true;
            var ran = false;
            try
            {
                using var scope = scopes.CreateScope();
                var bot = scope.ServiceProvider.GetRequiredService<ITelegramBot>();
                if (!bot.IsConfigured) continue;   // off or no token: nothing to watch
                ran = true;
                var links = scope.ServiceProvider.GetRequiredService<ChannelLinkService>();
                var updates = await bot.GetUpdatesAsync(offset, stoppingToken);
                if (updates is null) ok = false;
                else
                {
                    foreach (var update in updates)
                    {
                        try { await links.HandleUpdateAsync(update, stoppingToken); }
                        catch (Exception e) when (e is not OperationCanceledException) { ok = false; logger.LogWarning(e, "A channel update failed"); }
                        offset = Math.Max(offset, update.UpdateId + 1);   // confirms it, so Telegram does not send it again
                    }
                }
                var tenants = await scope.ServiceProvider.GetRequiredService<IAlertStore>().ListActiveTenantsAsync(stoppingToken);
                var dispatcher = scope.ServiceProvider.GetRequiredService<ChannelDispatcher>();
                foreach (var tenant in tenants)
                {
                    try { await dispatcher.DispatchTenantAsync(tenant, stoppingToken); }
                    catch (Exception e) when (e is not OperationCanceledException) { ok = false; logger.LogWarning(e, "Channel dispatch failed for one company"); }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ok = false;
                ran = true;
                logger.LogWarning(e, "Channel job failed");
            }
            if (ran) jobs.Report("channels", ok, TimeSpan.FromSeconds(seconds), clock.GetUtcNow());
        }
    }
}
