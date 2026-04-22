using System.Diagnostics;

namespace DotnetFleet.WorkerService.Execution;

public static class DeployerRunner
{
    /// <summary>
    /// Invokes DotnetDeployer in the given working directory using
    /// <c>dotnet dnx dotnetdeployer.tool -y</c> (.NET 10 on-demand tool runner).
    /// We invoke <c>dotnet dnx</c> (rather than the standalone <c>dnx</c> shim)
    /// because the <c>dnx</c> script is a thin wrapper around <c>dotnet dnx</c>
    /// that isn't always present (e.g. runtime-only installs, older .NET 10 previews,
    /// or service environments where it isn't on <c>PATH</c>).
    /// Captures stdout/stderr line by line via <paramref name="onLine"/>.
    /// Injects <paramref name="envVars"/> as extra environment variables for the child process.
    /// Returns true on exit code 0.
    /// </summary>
    public static async Task<(bool Success, string? Error)> RunAsync(
        string workingDirectory,
        Func<string, Task> onLine,
        IReadOnlyDictionary<string, string>? envVars = null,
        CancellationToken ct = default)
    {
        var dotnet = ResolveDotnetExecutable();

        var psi = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("dnx");
        psi.ArgumentList.Add("dotnetdeployer.tool");
        psi.ArgumentList.Add("-y");

        if (envVars is not null)
        {
            foreach (var (key, value) in envVars)
                psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };

        var outputLock = new SemaphoreSlim(1, 1);

        process.OutputDataReceived += async (_, e) =>
        {
            if (e.Data is null) return;
            await outputLock.WaitAsync(ct);
            try { await onLine(e.Data); }
            finally { outputLock.Release(); }
        };

        process.ErrorDataReceived += async (_, e) =>
        {
            if (e.Data is null) return;
            await outputLock.WaitAsync(ct);
            try { await onLine($"[ERR] {e.Data}"); }
            finally { outputLock.Release(); }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        if (process.ExitCode != 0)
            return (false, $"dotnetdeployer.tool exited with code {process.ExitCode}");

        return (true, null);
    }

    private static string ResolveDotnetExecutable()
    {
        var exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            var candidate = Path.Combine(dotnetRoot, exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        return "dotnet";
    }
}
