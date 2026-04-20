namespace DotnetFleet.Core.Domain;

public class Secret
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Environment variable name, e.g. GITHUB_TOKEN.</summary>
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    /// <summary>Null means the secret is global (shared across all projects).</summary>
    public Guid? ProjectId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
