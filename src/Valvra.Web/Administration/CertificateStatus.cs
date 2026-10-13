using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Security;

namespace Valvra.Web.Administration;

public sealed record CertificateStatusView(string Purpose, string Thumbprint, bool Active, string Status,
    DateTimeOffset? ExpiresAt = null, bool? HasPrivateKey = null);

public static class CertificateStatus
{
    public static IReadOnlyList<CertificateStatusView> Read(KeyProtectionOptions keys, AuditDatabaseOptions audit, IntegrityOptions integrity)
    {
        var result = new List<CertificateStatusView>();
        Add("Encryption", keys.ActiveThumbprint, keys.AllowedThumbprints);
        Add("Audit", audit.SigningCertificateThumbprint, audit.VerificationCertificateThumbprints);
        Add("Integrity", integrity.SigningCertificateThumbprint, integrity.VerificationCertificateThumbprints);
        return result;
        void Add(string purpose, string active, IEnumerable<string> retained)
        {
            var current = Normalize(active);
            if (current.Length == 0) result.Add(new(purpose,"",true,"Unconfigured"));
            foreach (var thumbprint in retained.Append(active).Select(Normalize).Where(x=>x.Length>0).Distinct(StringComparer.Ordinal))
                result.Add(Inspect(purpose,thumbprint,thumbprint==current));
        }
    }
    private static string Normalize(string value) => value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
    private static CertificateStatusView Inspect(string purpose, string thumbprint, bool active)
    {
        if (!OperatingSystem.IsWindows()) return new(purpose,thumbprint,active,"NotAssessed");
        try
        {
            using var store = new X509Store(StoreName.My,StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint,thumbprint,false);
            try
            {
                if (matches.Count != 1) return new(purpose,thumbprint,active,matches.Count==0 ? "Missing" : "Ambiguous");
                var cert = matches[0]; var expires = new DateTimeOffset(cert.NotAfter.ToUniversalTime());
                var now = DateTimeOffset.UtcNow;
                var status = expires <= now ? "Expired" : cert.NotBefore.ToUniversalTime() > now.UtcDateTime ? "NotYetValid"
                    : expires < now.AddDays(30) ? "Expiring" : "Present";
                // Presence is metadata only: no private key is opened/exported and key access is not tested.
                return new(purpose,thumbprint,active,status,expires,cert.HasPrivateKey);
            }
            finally { foreach (var cert in matches) cert.Dispose(); }
        }
        catch (Exception error) when (error is CryptographicException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(purpose,thumbprint,active,"Unavailable"); }
    }
}
