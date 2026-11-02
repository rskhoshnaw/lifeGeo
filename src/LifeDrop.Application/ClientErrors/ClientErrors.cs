using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.ClientErrors;

// Connection errors of the mobile app (plan step 5.10). A DNS or connection failure while the company is looked up or while signing in never reaches the server, so the app keeps it on the phone
// and sends it when the connection works again. The server stores it for the company (tenant) the app is now connected to, cleans the text (no URL, no token-like string, 300 characters) and
// keeps it 40 days. Managers with client_errors.read see the list in the panel. A password or token is never part of a report: the app does not send one and the server would not keep one.
public sealed record ClientErrorItem(DateTimeOffset? OccurredAt, string? Stage, string? ErrorKind, string? ErrorType, string? Message, string? EnteredCompanyCode, string? EnteredEmail,
    string? DeviceId, string? AppVersion, string? Platform);

public sealed record ReportClientErrorsRequest(string? CompanyCode, List<ClientErrorItem>? Items);

public sealed record ClientErrorDto(Guid Id, DateTimeOffset ReportedAt, DateTimeOffset OccurredAt, string Stage, string ErrorKind, string ErrorType, string Message,
    string? EnteredCompanyCode, string? EnteredEmail, string? DeviceId, string? AppVersion, string? Platform);

public sealed record ClientErrorQuery(string? Stage, DateTimeOffset? From, DateTimeOffset? To, int? Limit, DateTimeOffset? CursorReportedAt, Guid? CursorId);

// A cleaned report ready to store.
public sealed record ClientErrorRecord(DateTimeOffset OccurredAt, string Stage, string ErrorKind, string ErrorType, string Message, string? EnteredCompanyCode, string? EnteredEmail,
    string? DeviceId, string? AppVersion, string? Platform);

public enum ClientErrorResult { Success, Forbidden, NotFound, InvalidData }

public interface IClientErrorStore
{
    // Null when no active company has this code (the report is dropped).
    Task<TenantId?> FindTenantAsync(string companyCode, CancellationToken cancellationToken);
    // Stores the reports; for each one whose e-mail belongs to a user of the company an audit row (client_error.reported) is written in the same transaction.
    Task<int> AddAsync(TenantId tenantId, IReadOnlyList<ClientErrorRecord> records, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClientErrorDto>> ListAsync(TenantId tenantId, ClientErrorQuery query, int limit, CancellationToken cancellationToken);
    Task<int> PurgeAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken cancellationToken);
}

public static partial class ClientErrorText
{
    public const int MaxMessage = 300;

    [GeneratedRegex(@"[a-zA-Z][a-zA-Z0-9+.\-]*://\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Url();
    // Bearer/Basic credentials and key=value secrets.
    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+\S+|\b(password|passwd|pwd|token|secret|authorization|api[_-]?key)\b\s*[:=]\s*\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Secret();
    // Long unbroken token-like strings (JWT, API keys, hashes).
    [GeneratedRegex(@"[A-Za-z0-9_\-\.=+/]{24,}", RegexOptions.CultureInvariant)]
    private static partial Regex Tokenish();

    // Removes what must never be stored, collapses white space and cuts the text to the limit.
    public static string CleanMessage(string? text)
    {
        var value = text ?? "";
        value = Url().Replace(value, "<url>");
        value = Secret().Replace(value, "<redacted>");
        value = Tokenish().Replace(value, "<redacted>");
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value.Length <= MaxMessage ? value : value[..MaxMessage];
    }

    public static string? Cut(string? text, int max)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return null;
        return value.Length <= max ? value : value[..max];
    }
}

public sealed class ClientErrorReports(IClientErrorStore store, IPermissionStore permissions, TimeProvider clock)
{
    public const int MaxItems = 20;
    public const int MaxLimit = 200;
    public const int DefaultLimit = 50;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(40);
    private static readonly string[] Stages = ["company_lookup", "device_register", "login", "other"];
    private static readonly string[] Kinds = ["dns", "timeout", "tls", "http", "other"];

    // Saved reports: how many were stored. NotFound: the company code is unknown (nothing is stored, the app drops the reports).
    public async Task<(ClientErrorResult Result, int Stored)> ReportAsync(ReportClientErrorsRequest? request, CancellationToken cancellationToken)
    {
        var code = (request?.CompanyCode ?? "").Trim().ToLowerInvariant();
        if (request?.Items is not { Count: > 0 and <= MaxItems } items || code.Length is < 2 or > 40) return (ClientErrorResult.InvalidData, 0);
        var now = clock.GetUtcNow();
        var records = new List<ClientErrorRecord>();
        foreach (var item in items)
        {
            var stage = (item.Stage ?? "").Trim().ToLowerInvariant();
            var kind = (item.ErrorKind ?? "").Trim().ToLowerInvariant();
            if (!Stages.Contains(stage) || !Kinds.Contains(kind)) return (ClientErrorResult.InvalidData, 0);
            // The phone clock may be wrong: a time in the future or older than the retention becomes "now".
            var occurred = item.OccurredAt is { } t && t <= now.AddMinutes(5) && t >= now - Retention ? t : now;
            records.Add(new ClientErrorRecord(occurred, stage, kind, ClientErrorText.Cut(item.ErrorType, 100) ?? "unknown", ClientErrorText.CleanMessage(item.Message),
                ClientErrorText.Cut(item.EnteredCompanyCode, 60), ClientErrorText.Cut(item.EnteredEmail, 200), ClientErrorText.Cut(item.DeviceId, 100),
                ClientErrorText.Cut(item.AppVersion, 40), ClientErrorText.Cut(item.Platform, 40)));
        }
        if (await store.FindTenantAsync(code, cancellationToken) is not { } tenantId) return (ClientErrorResult.NotFound, 0);
        return (ClientErrorResult.Success, await store.AddAsync(tenantId, records, cancellationToken));
    }

    public async Task<(ClientErrorResult Result, IReadOnlyList<ClientErrorDto> Items)> ListAsync(TenantId tenantId, UserId actorId, ClientErrorQuery query, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty || !await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.ClientErrorsRead, cancellationToken))
            return (ClientErrorResult.Forbidden, []);
        var stage = string.IsNullOrWhiteSpace(query.Stage) ? null : query.Stage.Trim().ToLowerInvariant();
        if (stage is not null && !Stages.Contains(stage)) return (ClientErrorResult.InvalidData, []);
        if (query.Limit is < 1 or > MaxLimit || (query.CursorReportedAt is null) != (query.CursorId is null)) return (ClientErrorResult.InvalidData, []);
        return (ClientErrorResult.Success, await store.ListAsync(tenantId, query with { Stage = stage }, query.Limit ?? DefaultLimit, cancellationToken));
    }
}

// The 40-day retention, run by the background job for every company.
public sealed class ClientErrorRetention(IClientErrorStore store, TimeProvider clock)
{
    public Task<int> PurgeAsync(TenantId tenantId, CancellationToken cancellationToken) => store.PurgeAsync(tenantId, clock.GetUtcNow() - ClientErrorReports.Retention, cancellationToken);
}
