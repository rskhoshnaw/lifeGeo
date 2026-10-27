using LifeDrop.API.Observability;
using LifeDrop.Application.Alerts;
using LifeDrop.Application.Marketing;

namespace LifeDrop.API;

// Sends the queued messages of "api" marketing campaigns through the WhatsApp Business API (docs/WHATSAPP-API.md), OFF by default: it does nothing until WhatsApp:Enabled, the phone number id
// and the access token are set. Every WhatsApp:IntervalSeconds (default 10; 0 turns it off) it handles a small batch for every active company, with a pause between messages
// (WhatsApp:MessagesPerMinute, default 30). One company failing never stops the others.
public sealed class WhatsAppService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<WhatsAppService> logger, JobHealth jobs, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("WhatsApp:IntervalSeconds", 10);
        if (seconds <= 0) return;
        var perMinute = Math.Clamp(configuration.GetValue("WhatsApp:MessagesPerMinute", 30), 1, 600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var ok = true;
            var ran = false;
            try
            {
                using var scope = scopes.CreateScope();
                if (!scope.ServiceProvider.GetRequiredService<IWhatsAppCloud>().IsConfigured) continue;
                ran = true;
                var tenants = await scope.ServiceProvider.GetRequiredService<IAlertStore>().ListActiveTenantsAsync(stoppingToken);
                var dispatcher = new WhatsAppDispatcher(scope.ServiceProvider.GetRequiredService<IWhatsAppStore>(), scope.ServiceProvider.GetRequiredService<IWhatsAppCloud>(), clock)
                {
                    Spacing = TimeSpan.FromSeconds(60.0 / perMinute)
                };
                foreach (var tenant in tenants)
                {
                    try { await dispatcher.DispatchTenantAsync(tenant, stoppingToken); }
                    catch (Exception e) when (e is not OperationCanceledException) { ok = false; logger.LogWarning(e, "WhatsApp dispatch failed for one company"); }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ok = false;
                ran = true;
                logger.LogWarning(e, "WhatsApp job failed");
            }
            if (ran) jobs.Report("whatsapp", ok, TimeSpan.FromSeconds(seconds), clock.GetUtcNow());
        }
    }
}
