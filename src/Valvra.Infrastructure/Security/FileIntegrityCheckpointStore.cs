using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Valvra.Infrastructure.Security;

public sealed class CertificateIntegritySigner(IntegrityOptions options) : IIntegritySigner
{
    public IntegrityHead Sign(Guid installationId, long generation, string hash)
    {
        using var certificate = Load(options.SigningCertificateThumbprint);
        if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
            throw new CryptographicException("Integrity signing certificate expired or not yet valid.");
        using var rsa = certificate.GetRSAPrivateKey() ?? throw new CryptographicException("Integrity private key unavailable.");
        if (rsa.KeySize < 3072) throw new CryptographicException("Integrity RSA key must be at least 3072 bits.");
        var head = new IntegrityHead(installationId, generation, hash, Normalize(certificate.Thumbprint), []);
        return head with { Signature = rsa.SignData(Payload(head), HashAlgorithmName.SHA256, RSASignaturePadding.Pss) };
    }
    public bool Verify(IntegrityHead head)
    {
        if (head.Generation < 0 || head.Hash.Length != 64 || !options.VerificationCertificateThumbprints.Append(options.SigningCertificateThumbprint)
            .Any(x => Normalize(x) == Normalize(head.KeyId))) return false;
        using var certificate = Load(head.KeyId);
        using var rsa = certificate.GetRSAPublicKey() ?? throw new CryptographicException("Integrity public key unavailable.");
        return rsa.KeySize >= 3072 && rsa.VerifyData(Payload(head), head.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }
    public static byte[] Payload(IntegrityHead head) => Encoding.UTF8.GetBytes($"Valvra.Integrity.Head|1|{head.InstallationId:D}|{head.Generation}|{head.Hash}|{head.KeyId}");
    private static string Normalize(string value) => value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
    private static X509Certificate2 Load(string id)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, Normalize(id), false);
        try { if (matches.Count != 1) throw new CryptographicException("Integrity certificate missing or ambiguous."); return new(matches[0]); }
        finally { foreach (var cert in matches) cert.Dispose(); }
    }
}

public sealed class FileIntegrityCheckpointStore(string directory) : IIntegrityCheckpointStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Valvra.Integrity.Checkpoint.v1");
    private string CheckpointPath => Path.Combine(directory, "checkpoint.bin");
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(30)) { await Task.Delay(50, ct); }
        }
    }
    public async Task<IntegrityCheckpoint?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(CheckpointPath)) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Checkpoint requires Windows DPAPI.");
        var bytes = ProtectedData.Unprotect(await File.ReadAllBytesAsync(CheckpointPath, ct), Entropy, DataProtectionScope.LocalMachine);
        try { return JsonSerializer.Deserialize<IntegrityCheckpoint>(bytes) ?? throw new CryptographicException("Invalid checkpoint."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public async Task WriteAsync(IntegrityCheckpoint checkpoint, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Checkpoint requires Windows DPAPI.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint);
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
            var temporary = CheckpointPath + ".new";
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await file.WriteAsync(protectedBytes, ct); file.Flush(true); }
            File.Move(temporary, CheckpointPath, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
