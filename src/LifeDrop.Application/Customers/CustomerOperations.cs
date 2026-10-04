using System.Collections.Generic;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Customers;

public enum CreateCustomerResult { Created, DuplicateCode, Forbidden, InvalidData }

public sealed record CreateCustomerRequest(string Type, string Code, string Name);

public interface ICustomerStore
{
    // scope limits rows to the caller's data scope (plan step 0.6b); out-of-scope rows behave as not found.
    Task<CustomerDto?> GetByIdAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken);
    Task<CreateCustomerResult> CreateAsync(TenantId tenantId, UserId actorId, CustomerId id, CreateCustomerRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerDto>> ListAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken);
}

public sealed class CreateCustomer(ICustomerStore store)
{
    public async Task<(CreateCustomerResult Result, CustomerId Id)> ExecuteAsync(TenantId tenantId, UserId actorId, CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (CreateCustomerResult.Forbidden, CustomerId.Empty);
        if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            return (CreateCustomerResult.InvalidData, CustomerId.Empty);

        var id = CustomerId.New();
        var result = await store.CreateAsync(tenantId, actorId, id, request, cancellationToken);
        return (result, id);
    }
}

public sealed class GetCustomer(ICustomerStore store)
{
    public Task<CustomerDto?> ExecuteAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || id.IsEmpty) return Task.FromResult<CustomerDto?>(null);
        return store.GetByIdAsync(tenantId, id, scope, cancellationToken);
    }
}

public sealed class ListCustomers(ICustomerStore store)
{
    public async Task<(bool IsValid, IReadOnlyList<CustomerDto> Items)> ExecuteAsync(TenantId tenantId, AccessScope scope, int? limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty) return (false, Array.Empty<CustomerDto>());
        
        var actualLimit = limit ?? 20;
        if (actualLimit <= 0 || actualLimit > 100) return (false, Array.Empty<CustomerDto>());
        if (cursorCreatedAt.HasValue != cursorId.HasValue) return (false, Array.Empty<CustomerDto>());

        var items = await store.ListAsync(tenantId, scope, actualLimit, cursorCreatedAt, cursorId, cancellationToken);
        return (true, items);
    }
}
