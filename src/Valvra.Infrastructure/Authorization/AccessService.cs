using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Data;

namespace Valvra.Infrastructure.Authorization;

public sealed class AccessService(VaultDbContext db, TimeProvider clock)
{
    public async Task<HashSet<Guid>> GroupPathAsync(Guid groupId, CancellationToken ct)
    {
        var path = new HashSet<Guid>();
        Guid? current = groupId;
        while (current is { } id)
        {
            if (!path.Add(id) || path.Count > 128) throw new VaultUnavailableException("Ogiltig resurshierarki.");
            var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new AccessDeniedException();
            current = group.ParentId;
        }
        return path;
    }

    public async Task<VaultPermission> EffectiveAsync(Actor actor, TargetKind kind, Guid id, CancellationToken ct)
    {
        var (path, resourceId) = await ResolveTargetAsync(kind, id, ct);
        if (!actor.IsEnabled || await ExcludedAsync(actor, path, resourceId, ct)) return VaultPermission.None;
        if (actor.IsGlobalAdministrator) return (VaultPermission)31;
        var grants = await db.Grants.AsNoTracking().Where(x => path.Contains(x.TargetId)
            && x.TargetKind == TargetKind.Group || x.TargetKind == TargetKind.Resource && x.TargetId == resourceId).ToListAsync(ct);
        return PermissionEvaluator.Effective(actor, grants, path, resourceId, clock.GetUtcNow());
    }

    public async Task<bool> CanManageAsync(Actor actor, TargetKind kind, Guid id, CancellationToken ct)
    {
        var (path, resourceId) = await ResolveTargetAsync(kind, id, ct);
        if (!actor.IsEnabled || await ExcludedAsync(actor, path, resourceId, ct)) return false;
        if (actor.IsGlobalAdministrator) return true;
        var grants = await db.Grants.AsNoTracking().Where(x => path.Contains(x.TargetId)
            && x.TargetKind == TargetKind.Group || x.TargetKind == TargetKind.Resource && x.TargetId == resourceId).ToListAsync(ct);
        var owners = await db.Owners.AsNoTracking().Where(x => path.Contains(x.TargetId)
            && x.TargetKind == TargetKind.Group || x.TargetKind == TargetKind.Resource && x.TargetId == resourceId).ToListAsync(ct);
        return PermissionEvaluator.CanManage(actor, owners, path, resourceId,
            PermissionEvaluator.Effective(actor, grants, path, resourceId, clock.GetUtcNow()));
    }

    public async Task RequireAsync(Actor actor, TargetKind kind, Guid id, VaultPermission required, CancellationToken ct)
    {
        if (required == VaultPermission.ManageAccess)
        {
            if (!await CanManageAsync(actor, kind, id, ct)) throw new AccessDeniedException();
        }
        else if (!(await EffectiveAsync(actor, kind, id, ct)).HasFlag(required)) throw new AccessDeniedException();
    }

    public async Task<bool> IsExcludedAsync(Actor actor, TargetKind kind, Guid id, CancellationToken ct)
    {
        var (path, resourceId) = await ResolveTargetAsync(kind, id, ct);
        return await ExcludedAsync(actor, path, resourceId, ct);
    }

    private Task<bool> ExcludedAsync(Actor actor, HashSet<Guid> path, Guid? resourceId, CancellationToken ct) =>
        db.Exclusions.AsNoTracking().AnyAsync(x => x.Provider == actor.Provider && x.SubjectId == actor.SubjectId
            && (x.TargetKind == TargetKind.Group && path.Contains(x.TargetId)
                || x.TargetKind == TargetKind.Resource && x.TargetId == resourceId), ct);

    public async Task RequireLicenseAsync(Actor actor, Guid resourceId, VaultPermission required, CancellationToken ct)
    {
        // The global license role never supplies secret-read, password metadata or access-management rights.
        if (actor.IsEnabled && actor.IsLicenseAdministrator && (required & ~(VaultPermission.Metadata | VaultPermission.Modify | VaultPermission.ReadSecret)) == 0
            && !await IsExcludedAsync(actor, TargetKind.Resource, resourceId, ct))
        {
            // Combined operations still need actual content Read, while the license role supplies Modify.
            var contentRequired=required&VaultPermission.ReadSecret;
            if(contentRequired!=VaultPermission.None)await RequireAsync(actor,TargetKind.Resource,resourceId,contentRequired,ct);
            return;
        }
        await RequireAsync(actor, TargetKind.Resource, resourceId, required, ct);
    }

    private async Task<(HashSet<Guid> Path, Guid? ResourceId)> ResolveTargetAsync(TargetKind kind, Guid id, CancellationToken ct)
    {
        if (kind == TargetKind.Group) return (await GroupPathAsync(id, ct), null);
        if (kind != TargetKind.Resource) throw new AccessDeniedException();
        var resource = await db.Resources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AccessDeniedException();
        return (await GroupPathAsync(resource.GroupId, ct), resource.Id);
    }
}
