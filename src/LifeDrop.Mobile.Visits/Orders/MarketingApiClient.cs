namespace LifeDrop.Mobile.Visits;

public sealed record MarketingTemplateItem(Guid Id, string Name);
public sealed record MarketingCampaignItem(Guid Id, string Name, int Queued, int Sent, int Skipped);
public sealed record MarketingMessageItem(Guid Id, string CustomerName, string Phone, string Message);
public sealed record CampaignCreated(Guid Id, int Queued, int SkippedNoPhone);

// Marketing messages from the phone (plan step 5.7): templates, a campaign (a queue of customers), and the queue. The app opens each WhatsApp chat itself (wa.me); nothing is sent by the server.
public interface IMarketingApiClient
{
    Task<(VisitActionResult Result, IReadOnlyList<MarketingTemplateItem> Templates)> GetTemplatesAsync(CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, IReadOnlyList<MarketingCampaignItem> Campaigns)> GetCampaignsAsync(CancellationToken cancellationToken = default);
    // customerType empty = every type.
    Task<(VisitActionResult Result, CampaignCreated? Campaign)> CreateCampaignAsync(string name, Guid templateId, string? customerType, CancellationToken cancellationToken = default);
    // Only the messages that still wait.
    Task<(VisitActionResult Result, IReadOnlyList<MarketingMessageItem> Messages)> GetQueueAsync(Guid campaignId, CancellationToken cancellationToken = default);
    Task<VisitActionResult> SentAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task<VisitActionResult> SkipAsync(Guid messageId, CancellationToken cancellationToken = default);
}

// The pause the app asks for between two WhatsApp chats of a campaign: many messages in a row from one personal number can get it banned. The next chat opens after the pause.
public static class MarketingPacing
{
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(2);

    // Zero when nothing was sent yet or the pause is over.
    public static TimeSpan Remaining(DateTimeOffset? lastSent, DateTimeOffset now) =>
        lastSent is { } sent && now - sent < Pause ? Pause - (now - sent) : TimeSpan.Zero;
}

public sealed class MarketingApiClient : IMarketingApiClient
{
    private readonly OrdersHttp _http;

    public MarketingApiClient(HttpClient httpClient, LifeDrop.Mobile.Auth.IAuthSession session) =>
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));

    public async Task<(VisitActionResult Result, IReadOnlyList<MarketingTemplateItem> Templates)> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<TemplateDto>>(HttpMethod.Get, "/marketing/templates", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<MarketingTemplateItem>());
        if (items is null || items.Any(t => t is null || t.Id == Guid.Empty)) return (VisitActionResult.ServerError, Array.Empty<MarketingTemplateItem>());
        return (result, items.Select(t => new MarketingTemplateItem(t.Id, t.Name ?? "")).ToList());
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<MarketingCampaignItem> Campaigns)> GetCampaignsAsync(CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<CampaignDto>>(HttpMethod.Get, "/marketing/campaigns", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<MarketingCampaignItem>());
        if (items is null || items.Any(c => c is null || c.Id == Guid.Empty)) return (VisitActionResult.ServerError, Array.Empty<MarketingCampaignItem>());
        return (result, items.Select(c => new MarketingCampaignItem(c.Id, c.Name ?? "", c.Queued, c.Sent, c.Skipped)).ToList());
    }

    public async Task<(VisitActionResult Result, CampaignCreated? Campaign)> CreateCampaignAsync(string name, Guid templateId, string? customerType, CancellationToken cancellationToken = default)
    {
        var types = string.IsNullOrWhiteSpace(customerType) ? Array.Empty<string>() : [customerType];
        var (result, created) = await _http.CallAsync<CreatedDto>(HttpMethod.Post, "/marketing/campaigns", new { name, templateId, customerTypes = types }, cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        return created is null || created.Id == Guid.Empty ? (VisitActionResult.ServerError, null) : (result, new CampaignCreated(created.Id, created.Queued, created.SkippedNoPhone));
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<MarketingMessageItem> Messages)> GetQueueAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<MessageDto>>(HttpMethod.Get, $"/marketing/campaigns/{campaignId}/messages?status=queued", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<MarketingMessageItem>());
        if (items is null || items.Any(m => m is null || m.Id == Guid.Empty)) return (VisitActionResult.ServerError, Array.Empty<MarketingMessageItem>());
        return (result, items.Select(m => new MarketingMessageItem(m.Id, m.CustomerName ?? "", m.Phone ?? "", m.Message ?? "")).ToList());
    }

    public async Task<VisitActionResult> SentAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/marketing/messages/{messageId}/sent", null, cancellationToken)).Result;

    public async Task<VisitActionResult> SkipAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/marketing/messages/{messageId}/skip", null, cancellationToken)).Result;

    private sealed record TemplateDto(Guid Id, string? Name);
    private sealed record CampaignDto(Guid Id, string? Name, int Queued, int Sent, int Skipped);
    private sealed record CreatedDto(Guid Id, int Queued, int SkippedNoPhone);
    private sealed record MessageDto(Guid Id, string? CustomerName, string? Phone, string? Message);
}
