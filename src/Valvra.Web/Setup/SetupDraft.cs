namespace Valvra.Web.Setup;

// Field values are kept verbatim so even incomplete or invalid input can be resumed.
public sealed class SetupDraft
{
    public int Step { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new();

    private static readonly HashSet<string> AllowedFields = CreateAllowedFields();
    private static HashSet<string> CreateAllowedFields()
    {
        var fields = new HashSet<string>(StringComparer.Ordinal)
        {
            "hosts", "separateReader", "readerWindowsDomain", "readerWindowsUser",
            "provision", "adServer", "baseDn", "userBaseDn", "groupBaseDn", "testServer", "testName", "testDomain",
            "encryptionThumbprint", "auditThumbprint", "integrityThumbprint", "oldEncryptionThumbprints",
            "oldAuditThumbprints", "oldIntegrityThumbprints"
        };
        foreach (var prefix in new[] { "vault", "writer", "reader" })
            foreach (var suffix in new[] { "Provider", "Server", "Database", "Port", "Authentication", "Username", "ConnectTimeoutSeconds", "TrustServerCertificate", "MultiSubnetFailover" })
                fields.Add(prefix + suffix);
        foreach (var prefix in new[] { "vaultAdmin", "auditAdmin" })
            foreach (var suffix in new[] { "Authentication", "Username" }) fields.Add(prefix + suffix);
        return fields;
    }

    public SetupDraft Sanitized() => new()
    {
        Step = Math.Clamp(Step, 0, 4),
        Fields = (Fields ?? new()).Where(x => AllowedFields.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value ?? "", StringComparer.Ordinal)
    };
}
