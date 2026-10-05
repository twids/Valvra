using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Authorization;
using Valvra.Infrastructure.Data;

namespace Valvra.Infrastructure.Services;

public sealed class CredentialTestThrottle(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> attempts = new();
    private readonly object sync = new();
    public void Reserve(string profile, string username)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile + "|" + username.Trim().ToUpperInvariant())));
        lock (sync)
        {
            var now = clock.GetUtcNow();
            if (attempts.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(5))
                throw new VaultValidationException("LDAP-test för kontot kan göras högst en gång per fem minuter.");
            attempts[key] = now;
            foreach (var old in attempts.Where(x => now - x.Value > TimeSpan.FromHours(1)).ToArray()) attempts.TryRemove(old.Key, out _);
        }
    }
}

public sealed class CredentialTestService(VaultDbContext db, AccessService access, VaultService vault,
    AuditService audit, ICredentialTester tester, CredentialTestThrottle throttle)
{
    public async Task<CredentialTestResult> TestAsync(Actor actor, Guid entryId, string correlation, CancellationToken ct)
    {
        var entry = await db.Secrets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId && !x.Deleted, ct)
            ?? throw new AccessDeniedException();
        await access.RequireAsync(actor, TargetKind.Resource, entry.ResourceId, VaultPermission.TestCredential, ct);
        if (string.IsNullOrWhiteSpace(entry.LdapProfileId)) throw new VaultValidationException("Posten saknar LDAP-profil.");
        var payload = await vault.RevealSecretAsync(actor, entryId, null, "Ldap.TestRead", correlation, ct);
        throttle.Reserve(entry.LdapProfileId, payload.Username);
        await audit.RecordAsync(actor, "Ldap.Test", entryId, "AttemptAuthorized", correlation, ct);
        var result = await tester.TestAsync(entry.LdapProfileId, payload, ct);
        await audit.RecordAsync(actor, "Ldap.TestResult", entryId, result.ToString(), correlation, ct);
        return result;
    }
}
