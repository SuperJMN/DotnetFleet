namespace DotnetFleet.Core.Domain;

public class PackageArtifact
{
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
