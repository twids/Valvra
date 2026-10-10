using System.DirectoryServices.Protocols;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using Valvra.Core;

namespace Valvra.Infrastructure.Identity;

public sealed class ActiveDirectoryOptions
{
    public string Server { get; set; } = "";
    public int Port { get; set; } = 636;
    public string BaseDn { get; set; } = "";
    public string UserSearchBaseDn { get; set; } = "";
    public string GroupSearchBaseDn { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class WindowsIdentityProvider : IIdentityProvider
{
    public string ProviderId => "ad";
    public string? GetSubjectId(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;
        return principal.FindFirst(ClaimTypes.PrimarySid)?.Value
            ?? (OperatingSystem.IsWindows() && principal.Identity is WindowsIdentity windows ? windows.User?.Value : null);
    }
}

[SupportedOSPlatform("windows")]
public sealed class ActiveDirectoryProvider(ActiveDirectoryOptions options) : IDirectoryProvider
{
    public string ProviderId => "ad";

    public Task<Actor> ResolveAsync(string subjectId, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = Connect();
            var user = Search(connection, $"(&(objectCategory=person)(objectClass=user)(objectSid={SidFilter(subjectId)}))",
                "objectSid", "displayName", "sAMAccountName", "userAccountControl", "primaryGroupID").SingleOrDefault();
            if (user is null) throw new AccessDeniedException();
            var enabled = (int.Parse(Text(user, "userAccountControl"), System.Globalization.CultureInfo.InvariantCulture) & 2) == 0;
            if (!enabled) throw new AccessDeniedException();
            var groups = Search(connection, $"(&{Category(SubjectKind.Group)}(member:1.2.840.113556.1.4.1941:={Escape(user.DistinguishedName)}))", "objectSid")
                .Select(Sid).ToHashSet(StringComparer.Ordinal);
            var accountSid = new SecurityIdentifier(subjectId);
            var primaryRid = Text(user, "primaryGroupID");
            if (accountSid.AccountDomainSid is { } domain && int.TryParse(primaryRid, out var rid))
            {
                var primarySid = $"{domain.Value}-{rid}";
                var primary = Search(connection, $"(&{Category(SubjectKind.Group)}(objectSid={SidFilter(primarySid)}))", "objectSid").SingleOrDefault();
                if (primary is not null)
                {
                    groups.Add(primarySid);
                    foreach (var ancestor in Search(connection, $"(&{Category(SubjectKind.Group)}(member:1.2.840.113556.1.4.1941:={Escape(primary.DistinguishedName)}))", "objectSid"))
                        groups.Add(Sid(ancestor));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new Actor("ad", Sid(user), Text(user, "displayName", "sAMAccountName"), groups, true, false, false);
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            throw new VaultUnavailableException("Active Directory kan inte verifieras.", ex);
        }
    }, cancellationToken);

    public Task<IReadOnlyList<DirectorySubject>> SearchAsync(string query, SubjectKind kind, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<DirectorySubject>>(() =>
    {
        if (!Enum.IsDefined(kind) || query.Length is < 2 or > 128) throw new VaultValidationException("Ange 2–128 tecken för katalogsökning.");
        using var connection = Connect();
        var category = Category(kind);
        var term = Escape(query);
        var entries = SearchAt(connection, SearchBase(kind), $"(&{category}(|(sAMAccountName={term}*)(displayName={term}*)(cn={term}*)))", "objectSid", "displayName", "sAMAccountName", "cn");
        cancellationToken.ThrowIfCancellationRequested();
        return entries.Take(100).Select(x => new DirectorySubject("ad", Sid(x), Text(x, "displayName", "sAMAccountName", "cn"), kind)).ToArray();
    }, cancellationToken);

    public Task<DirectorySubject?> FindAsync(string subjectId, SubjectKind kind, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!Enum.IsDefined(kind)) throw new VaultValidationException("Ogiltig katalogtyp.");
        using var connection = Connect();
        var category = Category(kind);
        var entry = SearchAt(connection, SearchBase(kind), $"(&{category}(objectSid={SidFilter(subjectId)}))", "objectSid", "displayName", "sAMAccountName", "cn").SingleOrDefault();
        cancellationToken.ThrowIfCancellationRequested();
        return entry is null ? null : new DirectorySubject("ad", Sid(entry), Text(entry, "displayName", "sAMAccountName", "cn"), kind);
    }, cancellationToken);

    private LdapConnection Connect()
    {
        if (string.IsNullOrWhiteSpace(options.Server) || options.Server.Contains('/')
            || string.IsNullOrWhiteSpace(options.BaseDn) || options.Port is < 1 or > 65535
            || options.TimeoutSeconds is < 1 or > 30)
            throw new VaultUnavailableException("Active Directory är inte korrekt konfigurerat.");
        var connection = new LdapConnection(new LdapDirectoryIdentifier(options.Server, options.Port),
            CredentialCache.DefaultNetworkCredentials, AuthType.Negotiate)
        { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = true;
        connection.SessionOptions.Signing = true;
        connection.SessionOptions.Sealing = true;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        try { connection.Bind(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private List<SearchResultEntry> Search(LdapConnection connection, string filter, params string[] attributes)
        => SearchAt(connection, options.BaseDn, filter, attributes);
    private List<SearchResultEntry> SearchAt(LdapConnection connection, string searchBase, string filter, params string[] attributes)
    {
        var request = new SearchRequest(searchBase, filter, SearchScope.Subtree, attributes);
        var page = new PageResultRequestControl(250);
        request.Controls.Add(page);
        var results = new List<SearchResultEntry>();
        do
        {
            var response = (SearchResponse)connection.SendRequest(request);
            results.AddRange(response.Entries.Cast<SearchResultEntry>());
            page.Cookie = response.Controls.OfType<PageResultResponseControl>().SingleOrDefault()?.Cookie ?? [];
            if (results.Count > 10000) throw new VaultUnavailableException("Katalogresultatet överskrider säkerhetsgränsen.");
        } while (page.Cookie.Length > 0);
        return results;
    }
    public string SearchBase(SubjectKind kind) => kind == SubjectKind.User
        ? (string.IsNullOrWhiteSpace(options.UserSearchBaseDn) ? options.BaseDn : options.UserSearchBaseDn)
        : (string.IsNullOrWhiteSpace(options.GroupSearchBaseDn) ? options.BaseDn : options.GroupSearchBaseDn);
    public static string Category(SubjectKind kind) => kind == SubjectKind.Group
        ? "(&(objectCategory=group)(groupType:1.2.840.113556.1.4.803:=2147483648))"
        : "(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";

    public static string Escape(string value)
    {
        var result = new StringBuilder();
        foreach (var character in value)
            result.Append(character switch { '\\' => "\\5c", '*' => "\\2a", '(' => "\\28", ')' => "\\29", '\0' => "\\00", _ => character.ToString() });
        return result.ToString();
    }

    private static string SidFilter(string sid)
    {
        var value = new SecurityIdentifier(sid);
        var bytes = new byte[value.BinaryLength];
        value.GetBinaryForm(bytes, 0);
        return string.Concat(bytes.Select(x => $"\\{x:x2}"));
    }
    private static string Sid(SearchResultEntry entry) => new SecurityIdentifier((byte[])entry.Attributes["objectSid"][0], 0).Value;
    private static string Text(SearchResultEntry entry, params string[] names) => names
        .Select(name => entry.Attributes[name] is { Count: > 0 } attribute ? attribute[0]?.ToString() : null)
        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
}
