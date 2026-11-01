using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.ClientErrors;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests.ClientErrors;

// Connection errors reported by the mobile app (plan step 5.10).
public class ClientErrorReportsTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly UserId Manager = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static ClientErrorItem Item(string? message = "No such host is known", string? stage = "company_lookup", string? kind = "dns", DateTimeOffset? at = null) =>
        new(at ?? Now.AddMinutes(-5), stage, kind, "HttpRequestException", message, "Lifedrops", "panar@example.com", "device-1", "1.0.0", "android");

    private static ClientErrorReports Service(Store store, params string[] allowed) => new(store, new Permissions(allowed), new Clock());

    [Fact]
    public async Task ReportIsStoredForTheCompanyOfTheCodeWithCleanedText()
    {
        var store = new Store();
        var (result, stored) = await Service(store).ReportAsync(new ReportClientErrorsRequest(" LifeDrops ",
            [Item("Failed https://lifedrops.example.com/auth/login with Bearer abc.def password=hunter2 key eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIn0.sig  end")]), CancellationToken.None);
        Assert.Equal((ClientErrorResult.Success, 1), (result, stored));
        Assert.Equal("lifedrops", store.LookedUp);
        var message = Assert.Single(store.Added).Message;
        Assert.DoesNotContain("https", message);
        Assert.DoesNotContain("hunter2", message);
        Assert.DoesNotContain("eyJ", message);
        Assert.DoesNotContain("abc.def", message);
        Assert.Contains("Failed", message);
        Assert.Contains("end", message);
    }

    [Fact]
    public async Task LongTextIsCutAndFutureOrAncientTimesBecomeNow()
    {
        var store = new Store();
        var request = new ReportClientErrorsRequest("acme", [Item(string.Join(" ", Enumerable.Repeat("word", 200)), at: Now.AddDays(3)), Item(at: Now.AddDays(-90)), Item(at: Now.AddHours(-2))]);
        Assert.Equal(ClientErrorResult.Success, (await Service(store).ReportAsync(request, CancellationToken.None)).Result);
        Assert.Equal(ClientErrorText.MaxMessage, store.Added[0].Message.Length);
        Assert.Equal(Now, store.Added[0].OccurredAt);
        Assert.Equal(Now, store.Added[1].OccurredAt);
        Assert.Equal(Now.AddHours(-2), store.Added[2].OccurredAt);
    }

    [Fact]
    public async Task UnknownCompanyStoresNothing()
    {
        var store = new Store { KnownCode = "other" };
        var (result, stored) = await Service(store).ReportAsync(new ReportClientErrorsRequest("acme", [Item()]), CancellationToken.None);
        Assert.Equal((ClientErrorResult.NotFound, 0), (result, stored));
        Assert.Empty(store.Added);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("a", 1)]
    [InlineData("acme", 0)]
    [InlineData("acme", 21)]
    public async Task EmptyOversizedOrCodelessRequestIsInvalid(string? code, int count)
    {
        var store = new Store();
        var items = Enumerable.Range(0, count).Select(_ => Item()).ToList();
        Assert.Equal(ClientErrorResult.InvalidData, (await Service(store).ReportAsync(new ReportClientErrorsRequest(code, items), CancellationToken.None)).Result);
        Assert.Equal(ClientErrorResult.InvalidData, (await Service(store).ReportAsync(null, CancellationToken.None)).Result);
        Assert.Empty(store.Added);
    }

    [Theory]
    [InlineData("nowhere", "dns")]
    [InlineData("login", "weird")]
    [InlineData(null, "dns")]
    public async Task UnknownStageOrKindIsInvalid(string? stage, string kind)
    {
        var store = new Store();
        Assert.Equal(ClientErrorResult.InvalidData, (await Service(store).ReportAsync(new ReportClientErrorsRequest("acme", [Item(stage: stage, kind: kind)]), CancellationToken.None)).Result);
        Assert.Empty(store.Added);
    }

    [Fact]
    public async Task OnlyAUserWithThePermissionCanList()
    {
        var store = new Store();
        Assert.Equal(ClientErrorResult.Forbidden, (await Service(store).ListAsync(Tenant, Manager, new ClientErrorQuery(null, null, null, null, null, null), CancellationToken.None)).Result);
        var manager = Service(store, PermissionKeys.ClientErrorsRead);
        Assert.Equal(ClientErrorResult.Success, (await manager.ListAsync(Tenant, Manager, new ClientErrorQuery(" LOGIN ", null, null, 10, null, null), CancellationToken.None)).Result);
        Assert.Equal("login", store.ListedStage);
        Assert.Equal(ClientErrorResult.InvalidData, (await manager.ListAsync(Tenant, Manager, new ClientErrorQuery("bad", null, null, null, null, null), CancellationToken.None)).Result);
        Assert.Equal(ClientErrorResult.InvalidData, (await manager.ListAsync(Tenant, Manager, new ClientErrorQuery(null, null, null, 0, null, null), CancellationToken.None)).Result);
        Assert.Equal(ClientErrorResult.InvalidData, (await manager.ListAsync(Tenant, Manager, new ClientErrorQuery(null, null, null, null, Now, null), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task RetentionDeletesReportsOlderThanFortyDays()
    {
        var store = new Store();
        await new ClientErrorRetention(store, new Clock()).PurgeAsync(Tenant, CancellationToken.None);
        Assert.Equal(Now - TimeSpan.FromDays(40), store.PurgeCutoff);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Permissions(string[] allowed) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) => Task.FromResult(allowed.Contains(permissionKey));
    }

    private sealed class Store : IClientErrorStore
    {
        public string? KnownCode;
        public string? LookedUp;
        public string? ListedStage;
        public DateTimeOffset? PurgeCutoff;
        public readonly List<ClientErrorRecord> Added = [];

        public Task<TenantId?> FindTenantAsync(string companyCode, CancellationToken cancellationToken)
        {
            LookedUp = companyCode;
            return Task.FromResult<TenantId?>(KnownCode is null || KnownCode == companyCode ? Tenant : null);
        }

        public Task<int> AddAsync(TenantId tenantId, IReadOnlyList<ClientErrorRecord> records, CancellationToken cancellationToken)
        {
            Added.AddRange(records);
            return Task.FromResult(records.Count);
        }

        public Task<IReadOnlyList<ClientErrorDto>> ListAsync(TenantId tenantId, ClientErrorQuery query, int limit, CancellationToken cancellationToken)
        {
            ListedStage = query.Stage;
            return Task.FromResult<IReadOnlyList<ClientErrorDto>>([]);
        }

        public Task<int> PurgeAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            PurgeCutoff = cutoff;
            return Task.FromResult(0);
        }
    }
}
