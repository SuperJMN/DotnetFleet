using System.Globalization;
using System.Text;

namespace DotnetDeployer.Fleet.WorkerService.RepoStorage;

internal static class RepoDirectoryName
{
    private static readonly HashSet<char> InvalidFileNameChars = new(
        Path.GetInvalidFileNameChars()
            .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']));

    public static string Create(string name)
    {
        var normalized = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(ToSafeChar(c));
        }

        var result = builder.ToString().Trim('-', '_', '.');
        return result.Length == 0 ? "project" : result;
    }

    private static char ToSafeChar(char c)
    {
        if (char.IsWhiteSpace(c))
            return '-';

        if (c > 127 || char.IsControl(c) || InvalidFileNameChars.Contains(c))
            return '_';

        return c;
    }
}
