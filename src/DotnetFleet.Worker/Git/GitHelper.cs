using System.Diagnostics;

namespace DotnetFleet.WorkerService.Git;

public static class GitHelper
{
    /// <summary>
    /// Clones or updates a repo using the git CLI.
    /// Always fetches full history (required for GitVersion).
    /// Returns the local repo path.
    /// </summary>
    public static async Task CloneOrFetchAsync(
        string gitUrl,
        string branch,
        string localPath,
        Func<string, Task> log,
        CancellationToken ct = default)
    {
        bool isValidRepo = Directory.Exists(Path.Combine(localPath, ".git"));

        if (isValidRepo)
        {
            await log($"Fetching updates for {gitUrl} → {localPath}");
            await RunGitAsync(["fetch", "--all", "--tags", "--recurse-submodules", "--prune"], localPath, log, ct);
            await RunGitAsync(["checkout", branch], localPath, log, ct);
            await RunGitAsync(["reset", "--hard", $"origin/{branch}"], localPath, log, ct);
            await log($"Updated to latest {branch}");
        }
        else
        {
            await log($"Cloning {gitUrl} → {localPath}");

            if (Directory.Exists(localPath))
                Directory.Delete(localPath, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            // Full history clone (no --depth) so GitVersion works; recurse submodules
            await RunGitAsync(
                ["clone", "--recurse-submodules", "--branch", branch, gitUrl, localPath],
                workingDir: null,
                log, ct);

            await log("Clone complete");

            // Fetch all tags for GitVersion
            await RunGitAsync(["fetch", "--all", "--tags"], localPath, log, ct);
        }
    }

    /// <summary>Calculates the total size of a directory in bytes.</summary>
    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        return new DirectoryInfo(path)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }

    private static async Task RunGitAsync(
        string[] args,
        string? workingDir,
        Func<string, Task> log,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir ?? string.Empty
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var outText = await stdout;
        var errText = await stderr;

        foreach (var line in (outText + "\n" + errText).Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await log(line);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {errText.Trim()}");
    }
}
