using Valvra.Core;

namespace Valvra.Infrastructure.Identity;

public sealed class IdentitySelection
{
    public string LoginProviderId { get; set; } = "windows";
    public string DirectoryProviderId { get; set; } = "active-directory";
}
public sealed record ProviderDescription(string Id, string Name, string Namespace);
public sealed record LoginRegistration(ProviderDescription Description, Func<IServiceProvider, IIdentityProvider> Create);
public sealed record DirectoryRegistration(ProviderDescription Description, Func<ActiveDirectoryOptions, IDirectoryProvider> Create);

// Modules are registered by trusted application code, never loaded from paths
// or type names supplied by a browser. Authentication schemes belong to Web.
public sealed class ProviderRegistry(IReadOnlyList<LoginRegistration> logins, IReadOnlyList<DirectoryRegistration> directories)
{
    public IReadOnlyList<ProviderDescription> Logins => logins.Select(x => x.Description).ToArray();
    public IReadOnlyList<ProviderDescription> Directories => directories.Select(x => x.Description).ToArray();
    public LoginRegistration Login(string id) => logins.SingleOrDefault(x => x.Description.Id == id)
        ?? throw new VaultValidationException("Inloggningsprovidern är inte installerad.");
    public DirectoryRegistration Directory(string id) => directories.SingleOrDefault(x => x.Description.Id == id)
        ?? throw new VaultValidationException("Katalogprovidern är inte installerad.");
    public void Validate(IdentitySelection selection)
    {
        if (Login(selection.LoginProviderId).Description.Namespace != Directory(selection.DirectoryProviderId).Description.Namespace)
            throw new VaultValidationException("Inloggning och katalog måste använda samma identitetsnamnrymd.");
    }
    public static ProviderRegistry BuiltIn() => new(
        [new(new("windows", "Windows SSO", "ad"), _ => new WindowsIdentityProvider())],
        [new(new("active-directory", "Active Directory (LDAPS)", "ad"), options => OperatingSystem.IsWindows()
            ? new ActiveDirectoryProvider(options) : throw new PlatformNotSupportedException("AD requires Windows."))]);
}
public sealed class DirectoryConfiguration(ActiveDirectoryOptions initial)
{
    private ActiveDirectoryOptions current = Copy(initial);
    public ActiveDirectoryOptions Current => Copy(Volatile.Read(ref current));
    public void Apply(ActiveDirectoryOptions value) => Interlocked.Exchange(ref current, Copy(value));
    public static ActiveDirectoryOptions Copy(ActiveDirectoryOptions value) =>
        System.Text.Json.JsonSerializer.Deserialize<ActiveDirectoryOptions>(System.Text.Json.JsonSerializer.Serialize(value))!;
}
