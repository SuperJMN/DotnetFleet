namespace DotnetFleet.Core.Domain;

public static class ProjectIconPolicy
{
    public const long MaxIconBytes = 1_000_000;
    public const string SupportedFormatsDescription = "PNG, JPEG or ICO";

    public static bool IsSupportedFileName(string fileName) =>
        ContentTypeForExtension(Path.GetExtension(fileName)) is not null;

    public static string? ContentTypeForExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico" => "image/x-icon",
            _ => null
        };
    }
}
