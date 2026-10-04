using LifeDrop.Application.Customers;
using LifeDrop.Domain.Common;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LifeDrop.Application.Tests.Customers;

public class CustomerOperationsTests
{
    private sealed class FakeCustomerStore : ICustomerStore
    {
        public bool CreateCalled;
        public bool GetCalled;

        public Task<CreateCustomerResult> CreateAsync(TenantId tenantId, UserId actorId, CustomerId id, CreateCustomerRequest request, CancellationToken cancellationToken)
        {
            CreateCalled = true;
            return Task.FromResult(CreateCustomerResult.Created);
        }

        public Task<CustomerDto?> GetByIdAsync(TenantId tenantId, CustomerId id, CancellationToken cancellationToken)
        {
            GetCalled = true;
            return Task.FromResult<CustomerDto?>(new CustomerDto(id.Value, tenantId.Value, "Clinic", "C01", "Test Clinic", "no_location", DateTimeOffset.UtcNow));
        }

        public Task<System.Collections.Generic.IReadOnlyList<CustomerDto>> ListAsync(TenantId tenantId, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    [Fact]
    public async Task CreateCustomer_ValidRequest_CallsStore()
    {
        var store = new FakeCustomerStore();
        var sut = new CreateCustomer(store);
        
        var result = await sut.ExecuteAsync(TenantId.New(), UserId.New(), new CreateCustomerRequest("Clinic", "C01", "Test Clinic"), CancellationToken.None);
        
        Assert.Equal(CreateCustomerResult.Created, result.Result);
        Assert.True(store.CreateCalled);
    }

    [Theory]
    [InlineData("", "C01", "Name")]
    [InlineData("Type", "", "Name")]
    [InlineData("Type", "C01", "")]
    [InlineData(" ", " ", " ")]
    [InlineData(null, null, null)]
    public async Task CreateCustomer_MissingData_ReturnsInvalidData(string? type, string? code, string? name)
    {
        var store = new FakeCustomerStore();
        var sut = new CreateCustomer(store);
        
        var result = await sut.ExecuteAsync(TenantId.New(), UserId.New(), new CreateCustomerRequest(type!, code!, name!), CancellationToken.None);
        
        Assert.Equal(CreateCustomerResult.InvalidData, result.Result);
        Assert.False(store.CreateCalled);
    }

    [Fact]
    public async Task CreateCustomer_EmptyTenantOrActor_ReturnsForbidden()
    {
        var store = new FakeCustomerStore();
        var sut = new CreateCustomer(store);
        
        var result = await sut.ExecuteAsync(TenantId.Empty, UserId.Empty, new CreateCustomerRequest("Clinic", "C01", "Test Clinic"), CancellationToken.None);
        
        Assert.Equal(CreateCustomerResult.Forbidden, result.Result);
        Assert.False(store.CreateCalled);
    }

    [Fact]
    public async Task GetCustomer_ValidIds_CallsStore()
    {
        var store = new FakeCustomerStore();
        var sut = new GetCustomer(store);

        var result = await sut.ExecuteAsync(TenantId.New(), CustomerId.New(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(store.GetCalled);
    }

    [Fact]
    public async Task GetCustomer_EmptyTenantOrId_ReturnsNull()
    {
        var store = new FakeCustomerStore();
        var sut = new GetCustomer(store);

        var result = await sut.ExecuteAsync(TenantId.Empty, CustomerId.Empty, CancellationToken.None);

        Assert.Null(result);
        Assert.False(store.GetCalled);
    }

    private sealed class FakeListStore : ICustomerStore
    {
        public int LimitPassed;
        public DateTimeOffset? CursorDatePassed;
        public Guid? CursorIdPassed;
        public bool ListCalled;

        public Task<CreateCustomerResult> CreateAsync(TenantId tenantId, UserId actorId, CustomerId id, CreateCustomerRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<CustomerDto?> GetByIdAsync(TenantId tenantId, CustomerId id, CancellationToken cancellationToken) => throw new NotImplementedException();
        
        public Task<System.Collections.Generic.IReadOnlyList<CustomerDto>> ListAsync(TenantId tenantId, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken)
        {
            ListCalled = true;
            LimitPassed = limit;
            CursorDatePassed = cursorCreatedAt;
            CursorIdPassed = cursorId;
            return Task.FromResult((System.Collections.Generic.IReadOnlyList<CustomerDto>)Array.Empty<CustomerDto>());
        }
    }

    [Fact]
    public async Task ListCustomers_ValidRequest_CallsStoreWithDefaults()
    {
        var store = new FakeListStore();
        var sut = new ListCustomers(store);

        var result = await sut.ExecuteAsync(TenantId.New(), null, null, null, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.True(store.ListCalled);
        Assert.Equal(20, store.LimitPassed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task ListCustomers_InvalidLimit_ReturnsFalse(int limit)
    {
        var store = new FakeListStore();
        var sut = new ListCustomers(store);

        var result = await sut.ExecuteAsync(TenantId.New(), limit, null, null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.False(store.ListCalled);
    }

    [Fact]
    public async Task ListCustomers_PartialCursor_ReturnsFalse()
    {
        var store = new FakeListStore();
        var sut = new ListCustomers(store);

        var result1 = await sut.ExecuteAsync(TenantId.New(), 20, DateTimeOffset.UtcNow, null, CancellationToken.None);
        var result2 = await sut.ExecuteAsync(TenantId.New(), 20, null, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result1.IsValid);
        Assert.False(result2.IsValid);
        Assert.False(store.ListCalled);
    }

    [Fact]
    public async Task ListCustomers_EmptyTenant_ReturnsFalse()
    {
        var store = new FakeListStore();
        var sut = new ListCustomers(store);

        var result = await sut.ExecuteAsync(TenantId.Empty, 20, null, null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.False(store.ListCalled);
    }
}
