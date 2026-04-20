namespace DotnetFleet.Core.Domain;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Operator;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
