namespace Valvra.Core;

public static class PermissionEvaluator
{
    public static VaultPermission Effective(Actor actor, IEnumerable<AccessGrant> grants,
        IReadOnlySet<Guid> groupPath, Guid? resourceId, DateTimeOffset now)
    {
        if (!actor.IsEnabled) return VaultPermission.None;
        var permission = VaultPermission.None;
        foreach (var grant in grants)
        {
            if (!Matches(actor, grant.Provider, grant.SubjectKind, grant.SubjectId)) continue;
            if (grant.StartsAt is { } start && now < start) continue;
            if (grant.ExpiresAt is { } expiry && now >= expiry) continue;
            if (grant.TargetKind == TargetKind.Group && groupPath.Contains(grant.TargetId)
                || grant.TargetKind == TargetKind.Resource && grant.TargetId == resourceId)
                permission |= grant.Permissions;
        }
        return permission;
    }

    public static bool CanManage(Actor actor, IEnumerable<ResourceOwner> owners,
        IReadOnlySet<Guid> groupPath, Guid? resourceId, VaultPermission effective)
    {
        if (!actor.IsEnabled) return false;
        return actor.IsAccessAdministrator || effective.HasFlag(VaultPermission.ManageAccess)
            || owners.Any(owner => Matches(actor, owner.Provider, owner.SubjectKind, owner.SubjectId)
                && (owner.TargetKind == TargetKind.Group && groupPath.Contains(owner.TargetId)
                    || owner.TargetKind == TargetKind.Resource && owner.TargetId == resourceId));
    }

    public static bool Matches(Actor actor, string provider, SubjectKind kind, string id) =>
        string.Equals(actor.Provider, provider, StringComparison.Ordinal)
        && (kind == SubjectKind.User ? string.Equals(actor.SubjectId, id, StringComparison.Ordinal)
            : actor.GroupIds.Contains(id));

    public static void ValidateGrant(AccessGrant grant)
    {
        const VaultPermission all = VaultPermission.Metadata | VaultPermission.ReadSecret
            | VaultPermission.Modify | VaultPermission.ManageAccess | VaultPermission.TestCredential;
        if (grant.TargetKind is not (TargetKind.Group or TargetKind.Resource) || !Enum.IsDefined(grant.SubjectKind)
            || grant.Permissions == VaultPermission.None || (grant.Permissions & ~all) != 0
            || string.IsNullOrWhiteSpace(grant.SubjectId) || string.IsNullOrWhiteSpace(grant.Provider))
            throw new VaultValidationException("Ogiltig tilldelning.");
        if (grant.ExpiresAt is { } expiry)
        {
            if (grant.SubjectKind != SubjectKind.User
                || grant.Permissions != (VaultPermission.Metadata | VaultPermission.ReadSecret)
                || grant.StartsAt is not { } start || expiry <= start)
                throw new VaultValidationException("Tillfällig åtkomst gäller en användare och endast läsning, med giltigt tidsintervall.");
        }
    }
}
