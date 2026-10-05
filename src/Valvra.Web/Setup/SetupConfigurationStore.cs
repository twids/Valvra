using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valvra.Infrastructure.Auditing;
using Valvra.Infrastructure.Configuration;
using Valvra.Infrastructure.Identity;
using Valvra.Infrastructure.Security;

namespace Valvra.Web.Setup;

public sealed class InstallationSettings
{
    public string AllowedHosts { get; set; } = "localhost";
    public VaultDatabaseOptions VaultDatabase { get; set; } = new();
    public AuditDatabaseOptions AuditDatabase { get; set; } = new();
    public KeyProtectionOptions KeyProtection { get; set; } = new();
    public ActiveDirectoryOptions ActiveDirectory { get; set; } = new();
    public LdapTestOptions LdapTests { get; set; } = new();
}

public sealed class SetupConfigurationStore(string directory)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Valvra.Configuration.v1");
    private string SettingsPath => Path.Combine(directory, "installation.bin");
    private string BootstrapPath => Path.Combine(directory, "setup-token.sha256");
    public bool HasSettings => File.Exists(SettingsPath);
    public bool CanConfigure => File.Exists(BootstrapPath);

    public Stream? Load()
    {
        if (!HasSettings) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Configuration protection requires Windows DPAPI.");
        return new MemoryStream(ProtectedData.Unprotect(File.ReadAllBytes(SettingsPath), Entropy, DataProtectionScope.LocalMachine));
    }

    public string Initialize()
    {
        Directory.CreateDirectory(directory);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(BootstrapPath, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
        return token;
    }

    public bool ValidateToken(string? token)
    {
        if (!CanConfigure || token is null || token.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(File.ReadAllText(BootstrapPath).Trim()), SHA256.HashData(Encoding.UTF8.GetBytes(token))); }
        catch (FormatException) { return false; }
    }

    public void Save(InstallationSettings settings)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Configuration protection requires Windows DPAPI.");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(settings);
        try
        {
            Directory.CreateDirectory(directory);
            var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);
            var temporary = SettingsPath + ".new";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(protectedBytes); file.Flush(true); }
            File.Move(temporary, SettingsPath, overwrite: true);
            File.Delete(BootstrapPath);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
