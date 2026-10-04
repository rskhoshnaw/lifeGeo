using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Regions;

// Regions and teams (plan step 0.6a, decision D11): each team operates in exactly one region,
// the sales manager defines regions and assigns them to teams, and a user belongs to at most one team.
public sealed record RegionDto(Guid Id, string Name, DateTimeOffset CreatedAt);
public sealed record TeamDto(Guid Id, string Name, Guid RegionId, DateTimeOffset CreatedAt);

public sealed record CreateRegionRequest(string Name);
public sealed record CreateTeamRequest(string Name, Guid RegionId);
public sealed record ChangeTeamRegionRequest(Guid RegionId);

public enum RegionTeamResult { Success, Forbidden, InvalidData, NotFound, RegionNotFound, UserNotFound, Duplicate }

public interface IRegionTeamStore
{
    Task<RegionTeamResult> CreateRegionAsync(TenantId tenantId, UserId actorId, Guid regionId, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<RegionDto>> ListRegionsAsync(TenantId tenantId, CancellationToken cancellationToken);
    Task<RegionTeamResult> CreateTeamAsync(TenantId tenantId, UserId actorId, Guid teamId, string name, Guid regionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TeamDto>> ListTeamsAsync(TenantId tenantId, CancellationToken cancellationToken);
    Task<RegionTeamResult> ChangeTeamRegionAsync(TenantId tenantId, UserId actorId, Guid teamId, Guid regionId, CancellationToken cancellationToken);
    Task<RegionTeamResult> AssignMemberAsync(TenantId tenantId, UserId actorId, Guid teamId, Guid userId, CancellationToken cancellationToken);
    Task<RegionTeamResult> RemoveMemberAsync(TenantId tenantId, UserId actorId, Guid teamId, Guid userId, CancellationToken cancellationToken);
}

public sealed class RegionTeamService(IRegionTeamStore store, IPermissionStore permissions)
{
    public const int MaxNameLength = 100;

    public async Task<(RegionTeamResult Result, Guid Id)> CreateRegionAsync(TenantId tenantId, UserId actorId, CreateRegionRequest request, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.RegionsManage, cancellationToken))
            return (RegionTeamResult.Forbidden, Guid.Empty);
        if (!IsValidName(request.Name)) return (RegionTeamResult.InvalidData, Guid.Empty);

        var id = Guid.NewGuid();
        var result = await store.CreateRegionAsync(tenantId, actorId, id, request.Name.Trim(), cancellationToken);
        return (result, result == RegionTeamResult.Success ? id : Guid.Empty);
    }

    public async Task<(RegionTeamResult Result, IReadOnlyList<RegionDto> Items)> ListRegionsAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.RegionsRead, cancellationToken))
            return (RegionTeamResult.Forbidden, Array.Empty<RegionDto>());
        return (RegionTeamResult.Success, await store.ListRegionsAsync(tenantId, cancellationToken));
    }

    public async Task<(RegionTeamResult Result, Guid Id)> CreateTeamAsync(TenantId tenantId, UserId actorId, CreateTeamRequest request, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.TeamsManage, cancellationToken))
            return (RegionTeamResult.Forbidden, Guid.Empty);
        if (!IsValidName(request.Name) || request.RegionId == Guid.Empty) return (RegionTeamResult.InvalidData, Guid.Empty);

        var id = Guid.NewGuid();
        var result = await store.CreateTeamAsync(tenantId, actorId, id, request.Name.Trim(), request.RegionId, cancellationToken);
        return (result, result == RegionTeamResult.Success ? id : Guid.Empty);
    }

    public async Task<(RegionTeamResult Result, IReadOnlyList<TeamDto> Items)> ListTeamsAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.TeamsRead, cancellationToken))
            return (RegionTeamResult.Forbidden, Array.Empty<TeamDto>());
        return (RegionTeamResult.Success, await store.ListTeamsAsync(tenantId, cancellationToken));
    }

    // Moving a team to another region is a region assignment, so it needs regions.manage.
    public async Task<RegionTeamResult> ChangeTeamRegionAsync(TenantId tenantId, UserId actorId, Guid teamId, ChangeTeamRegionRequest request, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.RegionsManage, cancellationToken))
            return RegionTeamResult.Forbidden;
        if (teamId == Guid.Empty || request.RegionId == Guid.Empty) return RegionTeamResult.InvalidData;
        return await store.ChangeTeamRegionAsync(tenantId, actorId, teamId, request.RegionId, cancellationToken);
    }

    public async Task<RegionTeamResult> AssignMemberAsync(TenantId tenantId, UserId actorId, Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.TeamsManage, cancellationToken))
            return RegionTeamResult.Forbidden;
        if (teamId == Guid.Empty || userId == Guid.Empty) return RegionTeamResult.InvalidData;
        return await store.AssignMemberAsync(tenantId, actorId, teamId, userId, cancellationToken);
    }

    public async Task<RegionTeamResult> RemoveMemberAsync(TenantId tenantId, UserId actorId, Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.TeamsManage, cancellationToken))
            return RegionTeamResult.Forbidden;
        if (teamId == Guid.Empty || userId == Guid.Empty) return RegionTeamResult.InvalidData;
        return await store.RemoveMemberAsync(tenantId, actorId, teamId, userId, cancellationToken);
    }

    private async Task<bool> AllowedAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        !tenantId.IsEmpty && !actorId.IsEmpty &&
        await permissions.HasPermissionAsync(tenantId, actorId, permission, cancellationToken);

    private static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength;
}
