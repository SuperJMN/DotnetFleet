using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DotnetFleet.Coordinator.Auth;
using DotnetFleet.Core.Domain;
using Microsoft.Extensions.Configuration;

namespace DotnetFleet.Tests;

/// <summary>
/// Worker JWTs are the keystone of the Worker→Coordinator trust boundary:
/// every authorized endpoint reads <c>worker_id</c> from the token and verifies it
/// matches the route. These tests guard that contract.
/// </summary>
public class JwtServiceWorkerTokenTests
{
    private static JwtService BuildService() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-please-be-at-least-32-bytes-long!!",
                ["Jwt:Issuer"] = "TestIssuer",
                ["Jwt:Audience"] = "TestAudience",
                ["Jwt:ExpiryHours"] = "1"
            })
            .Build());

    [Fact]
    public void GenerateWorkerToken_emits_worker_id_and_Worker_role()
    {
        var jwt = BuildService();
        var worker = new Worker { Id = Guid.NewGuid(), Name = "test-worker" };

        var raw = jwt.GenerateWorkerToken(worker);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(raw);

        token.Claims.Should().Contain(c =>
            c.Type == "worker_id" && c.Value == worker.Id.ToString());
        token.Claims.Should().Contain(c =>
            c.Type == ClaimTypes.Role && c.Value == "Worker");
        token.Claims.Should().Contain(c =>
            c.Type == ClaimTypes.NameIdentifier && c.Value == worker.Id.ToString());
    }

    [Fact]
    public void GenerateWorkerToken_uses_configured_issuer_and_audience()
    {
        var jwt = BuildService();
        var raw = jwt.GenerateWorkerToken(new Worker { Id = Guid.NewGuid(), Name = "w" });
        var token = new JwtSecurityTokenHandler().ReadJwtToken(raw);

        token.Issuer.Should().Be("TestIssuer");
        token.Audiences.Should().ContainSingle().Which.Should().Be("TestAudience");
    }
}
