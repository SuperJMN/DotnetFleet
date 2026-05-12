namespace DotnetFleet.Tool;

internal static class ServiceCommandLine
{
    public static string BuildCoordinatorArgs(ServiceInstaller.CoordinatorInstallOptions opts)
    {
        var parts = new List<string>
        {
            $"--port {opts.Port}",
            $"--data-dir {QuoteArgument(opts.DataDir)}"
        };

        if (opts.Token != null) parts.Add($"--token {QuoteArgument(opts.Token)}");
        if (opts.JwtSecret != null) parts.Add($"--jwt-secret {QuoteArgument(opts.JwtSecret)}");
        if (opts.AdminPassword != null) parts.Add($"--admin-password {QuoteArgument(opts.AdminPassword)}");
        if (opts.Urls != null) parts.Add($"--urls {QuoteArgument(opts.Urls)}");
        if (opts.NoMdns) parts.Add("--no-mdns");

        return string.Join(" ", parts);
    }

    public static string BuildWorkerArgs(ServiceInstaller.WorkerInstallOptions opts)
    {
        var parts = new List<string>
        {
            $"--coordinator {QuoteArgument(opts.CoordinatorUrl)}",
            $"--name {QuoteArgument(opts.Name)}",
            $"--data-dir {QuoteArgument(opts.DataDir)}"
        };

        if (opts.Token != null) parts.Add($"--token {QuoteArgument(opts.Token)}");
        if (opts.PollInterval.HasValue) parts.Add($"--poll-interval {opts.PollInterval}");
        if (opts.MaxDisk.HasValue) parts.Add($"--max-disk {opts.MaxDisk}");

        return string.Join(" ", parts);
    }

    public static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    public static IReadOnlyList<string> Split(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var ch = commandLine[i];

            if (ch == '\\' && inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                i++;
                continue;
            }

            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                AddCurrent();
                continue;
            }

            current.Append(ch);
        }

        AddCurrent();
        return args;

        void AddCurrent()
        {
            if (current.Length == 0)
                return;

            args.Add(current.ToString());
            current.Clear();
        }
    }

    public static string? GetOptionValue(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                continue;

            return i + 1 < args.Count ? args[i + 1] : null;
        }

        return null;
    }
}
