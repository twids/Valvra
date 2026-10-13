using System.Reflection;

namespace Valvra.Web;

public static class BuildInformation
{
    public static string Build { get; } = typeof(BuildInformation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInformation).Assembly.GetName().Version?.ToString() ?? "unknown";
    public static string Version => Build.Split('+')[0];
}
