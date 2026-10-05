using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Valvra.Infrastructure.Security;

public sealed class WindowsConnectionCredentials
{
    public string Domain { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public static class WindowsCredentialRunner
{
    public static async Task<T> RunAsync<T>(WindowsConnectionCredentials? credentials, Func<Task<T>> action)
    {
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.Username)) return await action();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows credentials require Windows.");
        if (!LogonUser(credentials.Username, credentials.Domain, credentials.Password, 9, 3, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Audit reader Windows identity could not be initialized.");
        using (token) return await WindowsIdentity.RunImpersonatedAsync(token, action);
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LogonUserW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string username, string domain, string password, int logonType,
        int provider, out SafeAccessTokenHandle token);
}
