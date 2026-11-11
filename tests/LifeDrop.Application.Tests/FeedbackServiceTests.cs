using LifeDrop.Application.Feedback;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests;

// Feedback from the field (plan step 5.30): what a request must look like and who may do what.
public class FeedbackServiceTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly UserId Actor = UserId.New();
    private static readonly Guid Id = Guid.NewGuid();

    private sealed class Permissions(DataScope? manage = null, DataScope? customers = null) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<AccessScope?> GetAccessScopeAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult<AccessScope?>(permissionKey == PermissionKeys.FeedbackManage && manage is { } m ? new AccessScope(m, userId, null)
                : permissionKey == PermissionKeys.CustomersRead && customers is { } c ? new AccessScope(c, userId, null) : null);
    }

    private sealed class Store : IFeedbackStore
    {
        public string? Kind, Body, Status, PhotoType;
        public Guid? Customer;
        public FeedbackFilter? Filter;
        public bool LinkRefused;
        public bool ReplyFound = true;
        public Task<Guid?> CreateAsync(TenantId tenantId, UserId author, string kind, string body, Guid? customerId, AccessScope? customerScope, CancellationToken cancellationToken)
        {
            (Kind, Body, Customer) = (kind, body, customerId);
            return Task.FromResult<Guid?>(LinkRefused ? null : Id);
        }
        public Task<IReadOnlyList<FeedbackDto>> ListAsync(TenantId tenantId, FeedbackAccess access, FeedbackFilter filter, CancellationToken cancellationToken)
        {
            Filter = filter;
            return Task.FromResult<IReadOnlyList<FeedbackDto>>([]);
        }
        public Task<FeedbackDto?> GetAsync(TenantId tenantId, FeedbackAccess access, Guid id, CancellationToken cancellationToken) => Task.FromResult<FeedbackDto?>(null);
        public Task<FeedbackResult> AddPhotoAsync(TenantId tenantId, UserId author, Guid id, string contentType, byte[] data, int maxPhotos, CancellationToken cancellationToken)
        {
            PhotoType = contentType;
            return Task.FromResult(FeedbackResult.Success);
        }
        public Task<FeedbackPhoto?> GetPhotoAsync(TenantId tenantId, FeedbackAccess access, Guid id, Guid photoId, CancellationToken cancellationToken) => Task.FromResult<FeedbackPhoto?>(null);
        public Task<bool> AddReplyAsync(TenantId tenantId, UserId actor, AccessScope manage, Guid id, string body, CancellationToken cancellationToken)
        {
            Body = body;
            return Task.FromResult(ReplyFound);
        }
        public Task<bool> SetStatusAsync(TenantId tenantId, UserId actor, AccessScope manage, Guid id, string status, CancellationToken cancellationToken)
        {
            Status = status;
            return Task.FromResult(true);
        }
    }

    private static FeedbackService Service(Store store, DataScope? manage = null, DataScope? customers = null) => new(store, new Permissions(manage, customers));

    [Fact]
    public async Task Everybody_sends_feedback_the_kind_and_text_are_cleaned_and_checked()
    {
        var store = new Store();
        var (result, data) = await Service(store).CreateAsync(Tenant, Actor, new CreateFeedbackRequest(" Complaint ", "  slow orders  "), CancellationToken.None);
        Assert.Equal((FeedbackResult.Success, Id), (result, data!.Id));
        Assert.Equal(("complaint", "slow orders"), (store.Kind, store.Body));
        foreach (var bad in new[] { new CreateFeedbackRequest("praise", "x"), new CreateFeedbackRequest(null, "x"), new CreateFeedbackRequest("note", "  "), new CreateFeedbackRequest("note", null),
                     new CreateFeedbackRequest("note", new string('x', FeedbackService.MaxBody + 1)), new CreateFeedbackRequest("note", "x", Guid.Empty) })
            Assert.Equal(FeedbackResult.InvalidData, (await Service(new Store()).CreateAsync(Tenant, Actor, bad, CancellationToken.None)).Result);
        Assert.Equal(FeedbackResult.Success, (await Service(new Store()).CreateAsync(Tenant, Actor, new CreateFeedbackRequest("note", new string('x', FeedbackService.MaxBody)), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task A_customer_can_be_linked_only_by_a_user_who_reads_customers_and_only_one_in_scope()
    {
        var customer = Guid.NewGuid();
        var request = new CreateFeedbackRequest("note", "about a customer", customer);
        Assert.Equal(FeedbackResult.InvalidData, (await Service(new Store()).CreateAsync(Tenant, Actor, request, CancellationToken.None)).Result);                    // no customers.read
        var ok = new Store();
        Assert.Equal(FeedbackResult.Success, (await Service(ok, customers: DataScope.Own).CreateAsync(Tenant, Actor, request, CancellationToken.None)).Result);
        Assert.Equal(customer, ok.Customer);
        Assert.Equal(FeedbackResult.InvalidData, (await Service(new Store { LinkRefused = true }, customers: DataScope.Own).CreateAsync(Tenant, Actor, request, CancellationToken.None)).Result);   // not in scope
    }

    [Fact]
    public async Task Without_feedback_manage_the_list_is_only_the_own_and_filters_are_checked()
    {
        var store = new Store();
        await Service(store).ListAsync(Tenant, Actor, mine: false, "done", "complaint", null, null, null, 20, CancellationToken.None);
        Assert.Equal((true, "done", "complaint", 20), (store.Filter!.MineOnly, store.Filter.Status, store.Filter.Kind, store.Filter.Limit));
        var manager = new Store();
        await Service(manager, DataScope.Tenant).ListAsync(Tenant, Actor, null, null, null, null, null, null, null, CancellationToken.None);
        Assert.Equal((false, FeedbackService.DefaultLimit), (manager.Filter!.MineOnly, manager.Filter.Limit));
        await Service(manager, DataScope.Tenant).ListAsync(Tenant, Actor, mine: true, null, null, null, null, null, null, CancellationToken.None);
        Assert.True(manager.Filter.MineOnly);                                                                            // a manager can ask for only their own too
        foreach (var bad in new[] { ("open", (string?)null, 10), (null, "praise", 10), (null, null, 0), (null, null, 101) })
            Assert.Equal(FeedbackResult.InvalidData, (await Service(new Store()).ListAsync(Tenant, Actor, null, bad.Item1, bad.Item2, null, null, null, bad.Item3, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Answers_and_status_need_feedback_manage_and_a_valid_text_or_status()
    {
        Assert.Equal(FeedbackResult.Forbidden, await Service(new Store()).ReplyAsync(Tenant, Actor, Id, new ReplyFeedbackRequest("ok"), CancellationToken.None));
        Assert.Equal(FeedbackResult.Forbidden, await Service(new Store()).SetStatusAsync(Tenant, Actor, Id, new SetFeedbackStatusRequest("done"), CancellationToken.None));
        var store = new Store();
        var manager = Service(store, DataScope.Region);
        Assert.Equal(FeedbackResult.Success, await manager.ReplyAsync(Tenant, Actor, Id, new ReplyFeedbackRequest("  thanks  "), CancellationToken.None));
        Assert.Equal("thanks", store.Body);
        Assert.Equal(FeedbackResult.InvalidData, await manager.ReplyAsync(Tenant, Actor, Id, new ReplyFeedbackRequest(" "), CancellationToken.None));
        Assert.Equal(FeedbackResult.InvalidData, await manager.ReplyAsync(Tenant, Actor, Id, new ReplyFeedbackRequest(new string('x', FeedbackService.MaxReply + 1)), CancellationToken.None));
        Assert.Equal(FeedbackResult.NotFound, await Service(new Store { ReplyFound = false }, DataScope.Region).ReplyAsync(Tenant, Actor, Id, new ReplyFeedbackRequest("ok"), CancellationToken.None));
        Assert.Equal(FeedbackResult.Success, await manager.SetStatusAsync(Tenant, Actor, Id, new SetFeedbackStatusRequest(" DONE "), CancellationToken.None));
        Assert.Equal("done", store.Status);
        Assert.Equal(FeedbackResult.InvalidData, await manager.SetStatusAsync(Tenant, Actor, Id, new SetFeedbackStatusRequest("closed"), CancellationToken.None));
    }

    [Fact]
    public async Task A_photo_is_a_jpeg_png_or_webp_of_at_most_three_megabytes()
    {
        var store = new Store();
        var service = Service(store);
        Assert.Equal(FeedbackResult.Success, await service.AddPhotoAsync(Tenant, Actor, Id, "image/JPEG; charset=binary", [1, 2, 3], CancellationToken.None));
        Assert.Equal("image/jpeg", store.PhotoType);
        Assert.Equal(FeedbackResult.InvalidData, await service.AddPhotoAsync(Tenant, Actor, Id, "image/gif", [1], CancellationToken.None));
        Assert.Equal(FeedbackResult.InvalidData, await service.AddPhotoAsync(Tenant, Actor, Id, null, [1], CancellationToken.None));
        Assert.Equal(FeedbackResult.InvalidData, await service.AddPhotoAsync(Tenant, Actor, Id, "image/png", [], CancellationToken.None));
        Assert.Equal(FeedbackResult.InvalidData, await service.AddPhotoAsync(Tenant, Actor, Id, "image/png", new byte[FeedbackService.MaxPhotoBytes + 1], CancellationToken.None));
        Assert.Equal(FeedbackResult.Success, await service.AddPhotoAsync(Tenant, Actor, Id, "image/webp", new byte[FeedbackService.MaxPhotoBytes], CancellationToken.None));
    }
}
