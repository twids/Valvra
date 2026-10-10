using Valvra.Core;
using Valvra.Infrastructure.Security;

namespace Valvra.Infrastructure.Services;

// All public vault operations enter a verified snapshot before reading database state.
public sealed class VaultService(VaultOperations operations, VaultIntegrity integrity)
{
    public Task<IReadOnlyList<GroupView>> GroupsAsync(Actor actor, CancellationToken ct) =>
        integrity.RunAsync(() => operations.GroupsAsync(actor, ct), ct);

    public Task<IReadOnlyList<ResourceView>> ResourcesAsync(Actor actor, CancellationToken ct) =>
        integrity.RunAsync(() => operations.ResourcesAsync(actor, ct), ct);

    public Task<Guid> CreateGroupAsync(Actor actor, string name, Guid? parentId, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.CreateGroupAsync(actor, name, parentId, correlation, ct), ct);

    public Task<Guid> CreateResourceAsync(Actor actor, Guid groupId, string name, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.CreateResourceAsync(actor, groupId, name, correlation, ct), ct);

    public Task RenameGroupAsync(Actor actor, Guid id, string name, long revision, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.RenameGroupAsync(actor, id, name, revision, correlation, ct), ct);

    public Task RenameResourceAsync(Actor actor, Guid id, string name, long revision, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.RenameResourceAsync(actor, id, name, revision, correlation, ct), ct);

    public Task<IReadOnlyList<SecretView>> SecretsAsync(Actor actor, Guid resourceId, CancellationToken ct) =>
        integrity.RunAsync(() => operations.SecretsAsync(actor, resourceId, ct), ct);

    public Task<Guid> CreateSecretAsync(Actor actor, Guid resourceId, string title, SecretPayload payload,
        string? ldapProfileId, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.CreateSecretAsync(actor, resourceId, title, payload, ldapProfileId, correlation, ct), ct);

    public Task UpdateSecretAsync(Actor actor, Guid id, string title, SecretPayload payload,
        string? ldapProfileId, long revision, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.UpdateSecretAsync(actor, id, title, payload, ldapProfileId, revision, correlation, ct), ct);

    public Task<SecretPayload> RevealSecretAsync(Actor actor, Guid id, int? version, string action,
        string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.RevealSecretAsync(actor, id, version, action, correlation, ct), ct);

    public Task<IReadOnlyList<SecretVersionView>> VersionsAsync(Actor actor, Guid id, CancellationToken ct) =>
        integrity.RunAsync(() => operations.VersionsAsync(actor, id, ct), ct);

    public Task RestoreVersionAsync(Actor actor, Guid id, int version, long revision, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.RestoreVersionAsync(actor, id, version, revision, correlation, ct), ct);

    public Task SetDeletedAsync(Actor actor, Guid id, bool deleted, long revision, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.SetDeletedAsync(actor, id, deleted, revision, correlation, ct), ct);

    public Task<AccessView> AccessAsync(Actor actor, TargetKind kind, Guid id, CancellationToken ct) =>
        integrity.RunAsync(() => operations.AccessAsync(actor, kind, id, ct), ct);

    public Task<Guid> GrantAsync(Actor actor, AccessGrant grant, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.GrantAsync(actor, grant, correlation, ct), ct);

    public Task RevokeAsync(Actor actor, Guid grantId, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.RevokeAsync(actor, grantId, correlation, ct), ct);

    public Task<Guid> AddOwnerAsync(Actor actor, ResourceOwner owner, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.AddOwnerAsync(actor, owner, correlation, ct), ct);

    public Task RemoveOwnerAsync(Actor actor, Guid ownerId, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.RemoveOwnerAsync(actor, ownerId, correlation, ct), ct);

    public Task<AccessView> PreviewMoveAsync(Actor actor, TargetKind kind, Guid id, Guid destination, CancellationToken ct) =>
        integrity.RunAsync(() => operations.PreviewMoveAsync(actor, kind, id, destination, ct), ct);

    public Task MoveAsync(Actor actor, TargetKind kind, Guid id, Guid destination, long revision, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.MoveAsync(actor, kind, id, destination, revision, correlation, ct), ct);

    public Task<IReadOnlyList<LicenseView>> LicensesAsync(Actor actor, Guid resourceId, CancellationToken ct) =>
        integrity.RunAsync(() => operations.LicensesAsync(actor, resourceId, ct), ct);

    public Task<Guid> SaveLicenseAsync(Actor actor, Guid? id, Guid resourceId, string product, string vendor,
        string purchaseReference, int seats, DateTimeOffset? expiresAt, LicensePayload? payload, long revision, string correlation, CancellationToken ct,
        SecretChange secretChange = SecretChange.Replace) =>
        integrity.RunAsync(() => operations.SaveLicenseAsync(actor, id, resourceId, product, vendor, purchaseReference, seats, expiresAt, payload, revision, correlation, ct, secretChange), ct);

    public Task<LicensePayload> RevealLicenseAsync(Actor actor, Guid id, string correlation, CancellationToken ct, int? version = null) =>
        integrity.RunAsync(() => operations.RevealLicenseAsync(actor, id, correlation, ct, version), ct);

    public Task<IReadOnlyList<SecretVersionView>> LicenseVersionsAsync(Actor actor, Guid id, CancellationToken ct) =>
        integrity.RunAsync(() => operations.LicenseVersionsAsync(actor, id, ct), ct);

    public Task RestoreLicenseVersionAsync(Actor actor, Guid id, int version, long revision, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.RestoreLicenseVersionAsync(actor, id, version, revision, correlation, ct), ct);

    public Task<IReadOnlyList<LicenseAssignment>> AssignmentsAsync(Actor actor, Guid licenseId, CancellationToken ct) =>
        integrity.RunAsync(() => operations.AssignmentsAsync(actor, licenseId, ct), ct);

    public Task<Guid> AssignLicenseAsync(Actor actor, LicenseAssignment assignment, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.AssignLicenseAsync(actor, assignment, correlation, ct), ct);

    public Task UnassignLicenseAsync(Actor actor, Guid assignmentId, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.UnassignLicenseAsync(actor, assignmentId, correlation, ct), ct);

    public Task<IReadOnlyList<SecretView>> DeletedSecretsAsync(Actor actor, Guid resourceId, CancellationToken ct) =>
        integrity.RunAsync(() => operations.DeletedSecretsAsync(actor, resourceId, ct), ct);

    public Task DeleteEmptyTargetAsync(Actor actor, TargetKind kind, Guid id, long revision, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.DeleteEmptyTargetAsync(actor, kind, id, revision, correlation, ct), ct);

    public Task SetLicenseDeletedAsync(Actor actor, Guid id, bool deleted, long revision, string correlation, CancellationToken ct) =>
        integrity.RunAsync(() => operations.SetLicenseDeletedAsync(actor, id, deleted, revision, correlation, ct), ct);

    public Task<IReadOnlyList<LicenseView>> DeletedLicensesAsync(Actor actor, Guid resourceId, CancellationToken ct) =>
        integrity.RunAsync(() => operations.DeletedLicensesAsync(actor, resourceId, ct), ct);

}
