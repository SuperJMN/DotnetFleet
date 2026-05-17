using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace DotnetDeployer.Fleet.Tooling;

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
        if (OperatingSystem.IsWindows())
        {
            return IsWindowsAdministrator();
        }

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

        var procPath = Environment.ProcessPath;
        var cmdArgs = Environment.GetCommandLineArgs();
        if (string.IsNullOrEmpty(procPath) || cmdArgs.Length == 0)
        {
            Console.Error.WriteLine("✗ Could not determine current executable path; please re-run elevated manually.");
            return 1;
        }

        if (OperatingSystem.IsWindows())
        {
            return ReExecAsWindowsAdministrator(procPath, cmdArgs);
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
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

        // Re-invocation strategies:
        //   - `dotnet foo.dll user-args`: ProcessPath = dotnet, cmdArgs[0] = dll.
        //     We must re-pass the dll explicitly: `dotnet <dll> <user-args>`.
        //   - Native apphost (global tool): ProcessPath = apphost binary, cmdArgs[0]
        //     is the dll path injected by the runtime (NOT a user argument). We must
        //     re-invoke as `<apphost> <user-args>` and skip cmdArgs[0]; otherwise
        //     System.CommandLine sees the dll path as an unknown command.
        psi.ArgumentList.Add(procPath);

        var procName = GetExecutableNameWithoutExtension(procPath);
        var isDotnetHost = string.Equals(procName, "dotnet", StringComparison.OrdinalIgnoreCase);
        if (isDotnetHost && cmdArgs.Length > 0)
        {
            psi.ArgumentList.Add(cmdArgs[0]);
        }

        for (var i = 1; i < cmdArgs.Length; i++)
        {
            psi.ArgumentList.Add(cmdArgs[i]);
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

    internal static ProcessStartInfo BuildWindowsElevatedProcess(
        string processPath,
        IReadOnlyList<string> commandLineArgs,
        string workingDirectory)
    {
        return new ProcessStartInfo
        {
            FileName = processPath,
            Arguments = string.Join(" ", BuildReExecArguments(processPath, commandLineArgs).Select(QuoteWindowsArgument)),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = workingDirectory
        };
    }

    private static int ReExecAsWindowsAdministrator(string processPath, IReadOnlyList<string> commandLineArgs)
    {
        Console.Error.WriteLine("This command requires Administrator privileges. Requesting elevation...");
        Console.Error.WriteLine();

        try
        {
            using var proc = Process.Start(BuildWindowsElevatedProcess(
                processPath,
                commandLineArgs,
                Environment.CurrentDirectory));

            if (proc is null)
            {
                Console.Error.WriteLine("✗ Failed to launch elevated process.");
                return 1;
            }

            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"✗ Could not request elevation: {ex.Message}");
            Console.Error.WriteLine("  Re-run the command from an Administrator terminal.");
            return 1;
        }
    }

    private static IReadOnlyList<string> BuildReExecArguments(
        string processPath,
        IReadOnlyList<string> commandLineArgs)
    {
        var procName = GetExecutableNameWithoutExtension(processPath);
        var isDotnetHost = string.Equals(procName, "dotnet", StringComparison.OrdinalIgnoreCase);
        var args = new List<string>();

        if (isDotnetHost && commandLineArgs.Count > 0)
        {
            args.Add(commandLineArgs[0]);
        }

        for (var i = 1; i < commandLineArgs.Count; i++)
        {
            args.Add(commandLineArgs[i]);
        }

        return args;
    }

    private static string GetExecutableNameWithoutExtension(string processPath)
    {
        var separatorIndex = processPath.LastIndexOfAny(['\\', '/']);
        var fileName = separatorIndex >= 0 ? processPath[(separatorIndex + 1)..] : processPath;
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length == 0)
            return "\"\"";

        if (!argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return argument;

        var builder = new StringBuilder();
        builder.Append('"');
        var backslashes = 0;

        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static bool IsWindowsAdministrator()
    {
#pragma warning disable CA1416
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
    }
}
