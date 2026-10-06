using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Data;
using Valvra.Infrastructure.Security;

namespace Valvra.Infrastructure.Services;

public sealed class KeyRotationService(VaultDbContext db, ISecretCipher cipher, IKeyProtector keys, AuditService audit, VaultIntegrity integrity)
{
    public Task<int> RewrapAsync(Actor actor, CancellationToken ct) => integrity.RunAsync(() => RewrapCoreAsync(actor, ct), ct);
    private async Task<int> RewrapCoreAsync(Actor actor, CancellationToken ct)
    {
        if (!actor.IsEnabled || !actor.IsAccessAdministrator) throw new AccessDeniedException();
        var count = 0;
        // Operator command: run during a documented maintenance window, never as an unaudited SQL update.
        foreach (var version in await db.SecretVersions.OrderBy(x => x.EntryId).ThenBy(x => x.Version).ToListAsync(ct))
        {
            if (!NeedsRotation(version.EnvelopeJson)) continue;
            await audit.MutationAsync(actor, "Key.RewrapSecret", version.EntryId, "key-rotation", _ =>
            {
                version.EnvelopeJson = cipher.Rewrap(version.EnvelopeJson);
                return Task.FromResult(true);
            }, ct, JsonSerializer.Serialize(new { version.Version, NewKeyId = keys.ActiveKeyId }));
            count++;
        }
        foreach (var license in await db.Licenses.OrderBy(x => x.Id).ToListAsync(ct))
        {
            if (!NeedsRotation(license.EnvelopeJson)) continue;
            await audit.MutationAsync(actor, "Key.RewrapLicense", license.Id, "key-rotation", _ =>
            {
                license.EnvelopeJson = cipher.Rewrap(license.EnvelopeJson); license.Revision++;
                return Task.FromResult(true);
            }, ct, JsonSerializer.Serialize(new { license.SecretVersion, NewKeyId = keys.ActiveKeyId }));
            count++;
        }
        foreach (var version in await db.LicenseVersions.OrderBy(x => x.LicenseId).ThenBy(x => x.Version).ToListAsync(ct))
        {
            if (!NeedsRotation(version.EnvelopeJson)) continue;
            await audit.MutationAsync(actor, "Key.RewrapLicenseVersion", version.LicenseId, "key-rotation", _ =>
            {
                version.EnvelopeJson = cipher.Rewrap(version.EnvelopeJson);
                return Task.FromResult(true);
            }, ct, JsonSerializer.Serialize(new { version.Version, NewKeyId = keys.ActiveKeyId }));
            count++;
        }
        return count;
    }
    private bool NeedsRotation(string json) => JsonSerializer.Deserialize<CipherEnvelope>(json)?.KeyId != keys.ActiveKeyId;
}
