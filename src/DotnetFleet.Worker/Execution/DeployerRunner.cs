using System.Diagnostics;
using System.Threading.Channels;
using DotnetFleet.Core.Domain;

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
    /// Lines matching the <c>##deployer[phase.*]</c> protocol are parsed and routed
    /// to <paramref name="onPhase"/> instead (and NOT forwarded to <paramref name="onLine"/>).
    /// Injects <paramref name="envVars"/> as extra environment variables for the child process.
    /// Returns true on exit code 0.
    /// </summary>
    public static async Task<(bool Success, string? Error)> RunAsync(
        string workingDirectory,
        Func<string, Task> onLine,
        IReadOnlyDictionary<string, string>? envVars = null,
        Func<PhaseEvent, Task>? onPhase = null,
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

        EnsureDotnetEnvironment(psi);

        if (envVars is not null)
        {
            foreach (var (key, value) in envVars)
                psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };

        // Decouple stdout/stderr capture from log delivery.
        //
        // Process events fire on threadpool threads. The previous design used
        // `async void` event handlers that awaited an HTTP POST per line under a
        // SemaphoreSlim. During noisy commands like `dotnet workload restore`
        // (thousands of lines/sec on first run) this saturated the threadpool
        // on small machines (e.g. Raspberry Pi 4, 4 cores) and starved the
        // worker's heartbeat loop, which in turn caused the coordinator's
        // stale-job reaper to mark the worker as dead and fail the job.
        //
        // The fix: handlers do nothing but a non-blocking enqueue into an
        // unbounded channel. A single dedicated consumer task drains the
        // channel and awaits onLine sequentially — preserving order without
        // any lock and without blocking process I/O.
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) channel.Writer.TryWrite(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) channel.Writer.TryWrite($"[ERR] {e.Data}");
        };

        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    // Detect ##deployer[phase.*] markers and route them to onPhase.
                    // Markers are stripped from the line stream so they don't
                    // pollute the human-readable log.
                    if (onPhase is not null)
                    {
                        var ev = PhaseMarkerParser.TryParse(line);
                        if (ev is not null)
                        {
                            try { await onPhase(ev).ConfigureAwait(false); }
                            catch (OperationCanceledException) { throw; }
                            catch { /* never let phase-event delivery tear down the build */ }
                            continue;
                        }
                    }

                    try { await onLine(line).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* never let a transient log delivery failure tear down the build */ }
                }
            }
            catch (OperationCanceledException) { /* expected on cancellation */ }
        }, ct);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            channel.Writer.TryComplete();
            try { await consumer.ConfigureAwait(false); } catch { /* swallow */ }
            throw;
        }

        // Process has exited: signal end-of-stream and let the consumer drain
        // any buffered lines before we report completion.
        channel.Writer.TryComplete();
        try { await consumer.ConfigureAwait(false); } catch { /* already logged above */ }

        if (process.ExitCode != 0)
            return (false, $"dotnetdeployer.tool exited with code {process.ExitCode}");

        return (true, null);
    }

    /// <summary>
    /// Ensures the spawned <c>dotnet dnx</c> process — and every descendant it
    /// spawns (including <c>dotnet publish</c> and any <c>sh</c> launched by
    /// MSBuild <c>&lt;Exec&gt;</c> tasks) — has a usable .NET environment:
    /// <list type="bullet">
    ///   <item><description><c>DOTNET_ROOT</c> pointing to a directory that contains the <c>dotnet</c> host.</description></item>
    ///   <item><description><c>HOME</c> set so per-user tool/NuGet caches resolve correctly under systemd.</description></item>
    ///   <item><description><c>PATH</c> with <c>$DOTNET_ROOT</c> and <c>$HOME/.dotnet/tools</c> prepended,
    ///     so MSBuild <c>&lt;Exec&gt;</c> shell scripts can find <c>dotnet</c>, <c>dnx</c> and global tools.</description></item>
    /// </list>
    /// Without this, builds fail mid-publish with errors like
    /// <c>/usr/bin/sh: ...exec.cmd: dotnet: not found</c> (exit 127) when MSBuild
    /// post-build steps invoke <c>dotnet</c>.
    /// </summary>
    private static void EnsureDotnetEnvironment(ProcessStartInfo psi)
    {
        var separator = OperatingSystem.IsWindows() ? ';' : ':';

        var home = psi.Environment.TryGetValue("HOME", out var existingHome) && !string.IsNullOrEmpty(existingHome)
            ? existingHome
            : Environment.GetEnvironmentVariable("HOME")
              ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(home))
            psi.Environment["HOME"] = home;

        var dotnetRoot = ResolveDotnetRoot(psi, home);
        if (!string.IsNullOrEmpty(dotnetRoot))
            psi.Environment["DOTNET_ROOT"] = dotnetRoot;

        var current = psi.Environment.TryGetValue("PATH", out var existing) && !string.IsNullOrEmpty(existing)
            ? existing
            : Environment.GetEnvironmentVariable("PATH") ?? "";

        var parts = current.Split(separator, StringSplitOptions.RemoveEmptyEntries).ToList();

        void Prepend(string? dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            if (parts.Contains(dir, StringComparer.Ordinal)) return;
            parts.Insert(0, dir);
        }

        if (!string.IsNullOrEmpty(home))
            Prepend(Path.Combine(home, ".dotnet", "tools"));

        if (!string.IsNullOrEmpty(dotnetRoot))
            Prepend(dotnetRoot);

        psi.Environment["PATH"] = string.Join(separator, parts);
    }

    /// <summary>
    /// Resolves a usable <c>DOTNET_ROOT</c>: existing env var first, then the
    /// running runtime's location (works for self-contained / per-user installs),
    /// then <c>$HOME/.dotnet</c>, then a PATH lookup.
    /// </summary>
    private static string? ResolveDotnetRoot(ProcessStartInfo psi, string? home)
    {
        var env = psi.Environment.TryGetValue("DOTNET_ROOT", out var fromPsi) && !string.IsNullOrEmpty(fromPsi)
            ? fromPsi
            : Environment.GetEnvironmentVariable("DOTNET_ROOT");

        if (!string.IsNullOrEmpty(env) && DotnetHostExists(env))
            return env;

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrEmpty(runtimeDir))
        {
            var candidate = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
            if (DotnetHostExists(candidate))
                return candidate;
        }

        if (!string.IsNullOrEmpty(home))
        {
            var userDotnet = Path.Combine(home, ".dotnet");
            if (DotnetHostExists(userDotnet))
                return userDotnet;
        }

        var resolved = ResolveDotnetExecutable();
        if (Path.IsPathRooted(resolved))
        {
            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir) && DotnetHostExists(dir))
                return dir;
        }

        return null;
    }

    private static bool DotnetHostExists(string dir)
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return File.Exists(Path.Combine(dir, exe));
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
