using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace PeerlessPatcher.Patching;

/// <summary>
/// Checks whether the current process has sufficient privileges to write to game process memory.
/// </summary>
public static class PrivilegeCheck
{
    /// <summary>
    /// Returns true if the patcher can likely open game process handles with write access.
    /// On Windows this requires Administrator. On Linux it requires ptrace capability or root.
    /// </summary>
    public static bool HasSufficientPrivileges()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return IsWindowsAdmin();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return IsLinuxRoot();

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("linux")]
    private static bool IsLinuxRoot()
    {
        // getuid() == 0 means root
        return Environment.GetEnvironmentVariable("USER") == "root"
            || GetLinuxUid() == 0;
    }

    [SupportedOSPlatform("linux")]
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetLinuxUid();
}
