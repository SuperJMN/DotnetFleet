using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DotnetFleet.Core.Domain;
using Microsoft.IdentityModel.Tokens;

namespace DotnetFleet.Coordinator.Auth;

public class JwtService
{
    private readonly string secret;
    private readonly string issuer;
    private readonly string audience;
    private readonly int expiryHours;

    public JwtService(IConfiguration config)
    {
        secret = config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret is required");
        issuer = config["Jwt:Issuer"] ?? "DotnetFleet";
        audience = config["Jwt:Audience"] ?? "DotnetFleet";
        expiryHours = int.TryParse(config["Jwt:ExpiryHours"], out var h) ? h : 24;
    }

    public string GenerateToken(User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };
        return WriteToken(claims, TimeSpan.FromHours(expiryHours));
    }

    /// <summary>
    /// Issues a JWT for a worker. The token carries <c>Role=Worker</c> and a
    /// <c>worker_id</c> claim used by endpoints to authorize per-worker actions.
    /// </summary>
    public string GenerateWorkerToken(Worker worker)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, worker.Id.ToString()),
            new Claim(ClaimTypes.Name, worker.Name),
            new Claim(ClaimTypes.Role, "Worker"),
            new Claim("worker_id", worker.Id.ToString())
        };
        return WriteToken(claims, TimeSpan.FromHours(expiryHours));
    }

    private string WriteToken(IEnumerable<Claim> claims, TimeSpan lifetime)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
        ValidateIssuer = true,
        ValidIssuer = issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1)
    };
}
