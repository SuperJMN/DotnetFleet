using static BCrypt.Net.BCrypt;
using DotnetFleet.Coordinator.Auth;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DotnetFleet.Coordinator.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", Login);
        group.MapPost("/users", CreateUser).RequireAuthorization("Admin");
        group.MapGet("/users", ListUsers).RequireAuthorization("Admin");
        group.MapDelete("/users/{id:guid}", DeleteUser).RequireAuthorization("Admin");
    }

    private static async Task<IResult> Login(
        [FromBody] LoginRequest req,
        IFleetStorage storage,
        JwtService jwt)
    {
        var user = await storage.GetUserByUsernameAsync(req.Username.ToLower());
        if (user is null || !Verify(req.Password, user.PasswordHash))
            return Results.Unauthorized();

        var token = jwt.GenerateToken(user);
        return Results.Ok(new { token, username = user.Username, role = user.Role.ToString() });
    }

    private static async Task<IResult> CreateUser(
        [FromBody] CreateUserRequest req,
        IFleetStorage storage)
    {
        var existing = await storage.GetUserByUsernameAsync(req.Username.ToLower());
        if (existing is not null)
            return Results.Conflict(new { error = "Username already exists." });

        var user = new User
        {
            Username = req.Username.ToLower(),
            PasswordHash = HashPassword(req.Password),
            Role = Enum.TryParse<UserRole>(req.Role, true, out var role) ? role : UserRole.Operator
        };

        await storage.AddUserAsync(user);
        return Results.Created($"/api/auth/users/{user.Id}", new { user.Id, user.Username, user.Role });
    }

    private static async Task<IResult> ListUsers(IFleetStorage storage)
    {
        var users = await storage.GetUsersAsync();
        return Results.Ok(users.Select(u => new { u.Id, u.Username, u.Role, u.CreatedAt }));
    }

    private static async Task<IResult> DeleteUser(Guid id, IFleetStorage storage)
    {
        await storage.DeleteUserAsync(id);
        return Results.NoContent();
    }

    public record LoginRequest(string Username, string Password);
    public record CreateUserRequest(string Username, string Password, string Role = "Operator");
}
