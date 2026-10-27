using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Marketing;

// Sending marketing messages through the official WhatsApp Business API (Cloud API), see docs/WHATSAPP-API.md. OFF by default: it works only when WhatsApp:Enabled, WhatsApp:PhoneNumberId and
// WhatsApp:AccessToken are set. A message is a template that Meta approved; the first body variable {{1}} is the customer's name. Only customers who agreed (customers.whatsapp_opt_in) get
// one. The background job sends the queued messages of "api" campaigns one by one: Retry (temporary failure, rate limit) is tried again later, at most 3 times, Failed (Meta refused) is final.
public enum WhatsAppSendOutcome { Sent, Retry, Failed }

public sealed record WhatsAppSendResult(WhatsAppSendOutcome Outcome, string? MessageId = null, string? Error = null);

public interface IWhatsAppCloud
{
    bool IsConfigured { get; }
    // toDigits: international number, digits only (964750...). bodyParameter: the customer's name.
    Task<WhatsAppSendResult> SendTemplateAsync(string toDigits, string template, string language, string bodyParameter, CancellationToken cancellationToken);
}

public sealed record WhatsAppDue(Guid MessageId, string Phone, string CustomerName, string Template, string Language, int Attempts);

public interface IWhatsAppStore
{
    // Takes up to `limit` queued messages of "api" campaigns that are due, counts the attempt and hides them for 5 minutes (so a second job or a restart does not send them twice at once).
    Task<IReadOnlyList<WhatsAppDue>> ClaimDueAsync(TenantId tenantId, int limit, CancellationToken cancellationToken);
    Task MarkSentAsync(TenantId tenantId, Guid messageId, string? providerMessageId, CancellationToken cancellationToken);
    Task MarkRetryAsync(TenantId tenantId, Guid messageId, DateTimeOffset nextAttempt, string error, CancellationToken cancellationToken);
    Task MarkFailedAsync(TenantId tenantId, Guid messageId, string error, CancellationToken cancellationToken);
}

// The phone number in the form WhatsApp wants. Iraqi numbers are typed as 07xx xxx xxxx; with the country code and digits only, 0770... becomes 964770... (same rule as the app and the panel).
public static class WhatsAppPhone
{
    public const string CountryCode = "964";

    public static string? Digits(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        var international = phone.TrimStart().StartsWith('+') || digits.StartsWith("00", StringComparison.Ordinal);
        if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        else if (!international && digits.StartsWith('0')) digits = CountryCode + digits[1..];
        else if (!international && digits.Length == 10 && digits.StartsWith('7')) digits = CountryCode + digits;
        return digits.Length is >= 8 and <= 15 ? digits : null;
    }
}

public sealed class WhatsAppDispatcher(IWhatsAppStore store, IWhatsAppCloud cloud, TimeProvider clock)
{
    public const int MaxAttempts = 3;
    public const int BatchSize = 20;

    // Pause between two messages (the job sets it from WhatsApp:MessagesPerMinute); tests leave it at zero.
    public TimeSpan Spacing { get; init; } = TimeSpan.Zero;

    // After attempt n fails temporarily the next try comes later: 1 minute, then 10 minutes.
    public static TimeSpan Backoff(int attempts) => attempts <= 1 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10);

    // Returns how many messages were handled. Nothing is read or sent while the API is not configured.
    public async Task<int> DispatchTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        if (!cloud.IsConfigured) return 0;
        var due = await store.ClaimDueAsync(tenantId, BatchSize, cancellationToken);
        var first = true;
        foreach (var message in due)
        {
            if (!first && Spacing > TimeSpan.Zero) await Task.Delay(Spacing, cancellationToken);
            first = false;
            var digits = WhatsAppPhone.Digits(message.Phone);
            if (digits is null)
            {
                await store.MarkFailedAsync(tenantId, message.MessageId, "invalid_phone", cancellationToken);
                continue;
            }
            WhatsAppSendResult result;
            try { result = await cloud.SendTemplateAsync(digits, message.Template, message.Language, message.CustomerName, cancellationToken); }
            catch (Exception e) when (e is not OperationCanceledException) { result = new WhatsAppSendResult(WhatsAppSendOutcome.Retry, Error: "exception"); }
            switch (result.Outcome)
            {
                case WhatsAppSendOutcome.Sent:
                    await store.MarkSentAsync(tenantId, message.MessageId, result.MessageId, cancellationToken);
                    break;
                case WhatsAppSendOutcome.Retry when message.Attempts < MaxAttempts:
                    await store.MarkRetryAsync(tenantId, message.MessageId, clock.GetUtcNow() + Backoff(message.Attempts), result.Error ?? "retry", cancellationToken);
                    break;
                default:
                    await store.MarkFailedAsync(tenantId, message.MessageId, result.Error ?? "failed", cancellationToken);
                    break;
            }
        }
        return due.Count;
    }
}
