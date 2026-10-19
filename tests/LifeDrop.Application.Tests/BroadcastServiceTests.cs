using LifeDrop.Application.Notifications;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests;

public class BroadcastServiceTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly UserId Actor = UserId.New();
    private static readonly Guid RegionA = Guid.NewGuid();

    private sealed class Permissions(DataScope? scope, Guid? region = null) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) => Task.FromResult(scope is not null);
        public Task<AccessScope?> GetAccessScopeAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(scope is { } s && permissionKey == PermissionKeys.NotificationsSend ? new AccessScope(s, userId, region) : null);
    }

    private sealed class Store : IBroadcastStore
    {
        public List<(MessageAudience Audience, string Message)> Sent = [];
        public Task<int> SendAsync(TenantId tenantId, UserId actorId, MessageAudience audience, string message, CancellationToken cancellationToken)
        {
            Sent.Add((audience, message));
            return Task.FromResult(3);
        }
    }

    private static BroadcastService Service(Store store, DataScope? scope = DataScope.Tenant, Guid? region = null) => new(store, new Permissions(scope, region));

    private static Task<(SendMessageOutcome Result, SendMessageResultDto? Sent)> Send(BroadcastService service, SendMessageRequest request) =>
        service.SendAsync(Tenant, Actor, request, CancellationToken.None);

    [Fact]
    public async Task A_manager_reaches_everybody_a_group_or_chosen_people()
    {
        var store = new Store();
        var service = Service(store);
        var a = Guid.NewGuid();
        var team = Guid.NewGuid();
        var (everybody, sent) = await Send(service, new SendMessageRequest("  Meeting at 9 ", null, null, null, null, true));
        Assert.Equal(SendMessageOutcome.Success, everybody);
        Assert.Equal(3, sent!.Recipients);
        Assert.Equal(SendMessageOutcome.Success, (await Send(service, new SendMessageRequest("Hi", null, " SalesRep ", RegionA, team, null))).Result);
        Assert.Equal(SendMessageOutcome.Success, (await Send(service, new SendMessageRequest("Hi", [a, a], null, null, null, null))).Result);
        Assert.Equal("Meeting at 9", store.Sent[0].Message);
        Assert.Equal(("SalesRep", (Guid?)RegionA, (Guid?)team, (Guid?)null), (store.Sent[1].Audience.Role, store.Sent[1].Audience.RegionId, store.Sent[1].Audience.TeamId, store.Sent[1].Audience.OnlyRegionId));
        Assert.Equal(new[] { a }, store.Sent[2].Audience.UserIds!.ToArray());   // duplicates are dropped
    }

    [Fact]
    public async Task A_supervisor_is_limited_to_their_own_region()
    {
        var store = new Store();
        Assert.Equal(SendMessageOutcome.Success, (await Send(Service(store, DataScope.Region, RegionA), new SendMessageRequest("Hi", null, null, null, null, true))).Result);
        Assert.Equal(RegionA, store.Sent[0].Audience.OnlyRegionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(DataScope.Own)]
    public async Task Without_the_permission_or_with_own_scope_nothing_is_sent(DataScope? scope)
    {
        var store = new Store();
        Assert.Equal(SendMessageOutcome.Forbidden, (await Send(Service(store, scope), new SendMessageRequest("Hi", null, null, null, null, true))).Result);
        Assert.Equal(SendMessageOutcome.Forbidden, (await Send(Service(store, DataScope.Region, null), new SendMessageRequest("Hi", null, null, null, null, true))).Result);
        Assert.Empty(store.Sent);
    }

    [Fact]
    public async Task A_message_needs_text_and_exactly_one_kind_of_audience()
    {
        var store = new Store();
        var service = Service(store);
        var invalid = new[]
        {
            new SendMessageRequest(null, null, null, null, null, true),
            new SendMessageRequest("  ", null, null, null, null, true),
            new SendMessageRequest(new string('x', BroadcastService.MaxMessageLength + 1), null, null, null, null, true),
            new SendMessageRequest("Hi", null, null, null, null, null),                                  // nobody named
            new SendMessageRequest("Hi", null, "SalesRep", null, null, true),                           // everybody and a filter
            new SendMessageRequest("Hi", [Guid.NewGuid()], "SalesRep", null, null, null),               // people and a filter
            new SendMessageRequest("Hi", [], null, null, null, null),
            new SendMessageRequest("Hi", [Guid.Empty], null, null, null, null),
            new SendMessageRequest("Hi", null, null, Guid.Empty, null, null),
        };
        foreach (var request in invalid) Assert.Equal(SendMessageOutcome.InvalidData, (await Send(service, request)).Result);
        Assert.Empty(store.Sent);
    }
}
