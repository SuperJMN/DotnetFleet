using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotnetFleet.Tool;

/// <summary>
/// Re-executes the current process under <c>sudo</c> when root is required,
/// preserving <c>PATH</c>, <c>DOTNET_ROOT</c> and <c>HOME</c> so that
/// per-user .NET installs (e.g. <c>~/.dotnet/dotnet</c>) keep working.
/// </summary>
public static class SudoElevation
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    public static bool IsRoot()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return true;
        }

        try
        {
            return GetEuid() == 0;
        }
        catch
        {
            return string.Equals(Environment.UserName, "root", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// If not running as root on Linux/macOS, re-executes the current command line
    /// via <c>sudo env …</c> and returns its exit code. Returns <c>null</c> if no
    /// elevation was needed (caller continues normally).
    /// </summary>
    public static int? ReExecAsRootIfNeeded()
    {
        if (IsRoot())
        {
            return null;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        var procPath = Environment.ProcessPath;
        var cmdArgs = Environment.GetCommandLineArgs();
        if (string.IsNullOrEmpty(procPath) || cmdArgs.Length == 0)
        {
            Console.Error.WriteLine("✗ Could not determine current executable path; please re-run with sudo manually.");
            return 1;
        }

        Console.Error.WriteLine("This command requires root. Re-running with sudo (you may be prompted for your password)…");
        Console.Error.WriteLine();

        var psi = new ProcessStartInfo("sudo")
        {
            UseShellExecute = false
        };

        psi.ArgumentList.Add("env");

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            psi.ArgumentList.Add($"PATH={path}");
        }

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            psi.ArgumentList.Add($"DOTNET_ROOT={dotnetRoot}");
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            psi.ArgumentList.Add($"HOME={home}");
        }

        // When launched via `dotnet foo.dll`, ProcessPath is the dotnet host
        // and args[0] is the dll path. Re-invoke as `dotnet <dll> <user-args>`.
        // For a native apphost (global tool), ProcessPath == args[0]; pass it once.
        if (!string.Equals(procPath, cmdArgs[0], StringComparison.Ordinal))
        {
            psi.ArgumentList.Add(procPath);
            foreach (var a in cmdArgs)
            {
                psi.ArgumentList.Add(a);
            }
        }
        else
        {
            psi.ArgumentList.Add(procPath);
            for (var i = 1; i < cmdArgs.Length; i++)
            {
                psi.ArgumentList.Add(cmdArgs[i]);
            }
        }

        try
        {
            var proc = Process.Start(psi);
            if (proc is null)
            {
                Console.Error.WriteLine("✗ Failed to launch sudo.");
                return 1;
            }

            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"✗ Could not invoke sudo: {ex.Message}");
            Console.Error.WriteLine("  Re-run the command manually with sudo.");
            return 1;
        }
    }
}
