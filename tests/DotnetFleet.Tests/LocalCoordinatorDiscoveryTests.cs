using System.Text.Json;
using DotnetFleet.Tool;

namespace DotnetFleet.Tests;

public class LocalCoordinatorDiscoveryTests : IDisposable
{
    private readonly string tempRoot;

    public LocalCoordinatorDiscoveryTests()
    {
        tempRoot = Path.Combine(Path.GetTempPath(), "fleet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void TryFromUserHome_ReturnsResult_WhenConfigExists()
    {
        var fakeHome = Path.Combine(tempRoot, "home");
        var coordDir = Path.Combine(fakeHome, ".fleet", "coordinator");
        Directory.CreateDirectory(coordDir);

        WriteConfig(Path.Combine(coordDir, "config.json"),
            jwtSecret: "jwt", token: "token-abc", port: 5123);

        var prevHome = Environment.GetEnvironmentVariable("HOME");
        var prevUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        Environment.SetEnvironmentVariable("HOME", fakeHome);
        Environment.SetEnvironmentVariable("USERPROFILE", fakeHome);
        Environment.SetEnvironmentVariable("SUDO_USER", null);

        try
        {
            var result = LocalCoordinatorDiscovery.TryFromUserHome();

            result.Should().NotBeNull();
            result!.Url.Should().Be("http://localhost:5123");
            result.Token.Should().Be("token-abc");
            result.Source.Should().Contain("config.json");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", prevHome);
            Environment.SetEnvironmentVariable("USERPROFILE", prevUserProfile);
        }
    }

    [Fact]
    public void TryFromUserHome_ReturnsNull_WhenNoConfig()
    {
        var fakeHome = Path.Combine(tempRoot, "empty-home");
        Directory.CreateDirectory(fakeHome);

        var prevHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", fakeHome);
        Environment.SetEnvironmentVariable("USERPROFILE", fakeHome);
        Environment.SetEnvironmentVariable("SUDO_USER", null);

        try
        {
            var result = LocalCoordinatorDiscovery.TryFromUserHome();
            result.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", prevHome);
        }
    }

    [Fact]
    public void TryFromUserHome_ReturnsNull_WhenConfigMissingToken()
    {
        var fakeHome = Path.Combine(tempRoot, "home2");
        var coordDir = Path.Combine(fakeHome, ".fleet", "coordinator");
        Directory.CreateDirectory(coordDir);
        File.WriteAllText(Path.Combine(coordDir, "config.json"),
            "{ \"jwtSecret\": \"x\", \"port\": 5000 }");

        var prevHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", fakeHome);
        Environment.SetEnvironmentVariable("USERPROFILE", fakeHome);
        Environment.SetEnvironmentVariable("SUDO_USER", null);

        try
        {
            var result = LocalCoordinatorDiscovery.TryFromUserHome();
            result.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", prevHome);
        }
    }

    [Fact]
    public void TryFromUserHome_HandlesCorruptedConfigGracefully()
    {
        var fakeHome = Path.Combine(tempRoot, "home3");
        var coordDir = Path.Combine(fakeHome, ".fleet", "coordinator");
        Directory.CreateDirectory(coordDir);
        File.WriteAllText(Path.Combine(coordDir, "config.json"), "not-json{");

        var prevHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", fakeHome);
        Environment.SetEnvironmentVariable("USERPROFILE", fakeHome);
        Environment.SetEnvironmentVariable("SUDO_USER", null);

        try
        {
            var result = LocalCoordinatorDiscovery.TryFromUserHome();
            result.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", prevHome);
        }
    }

    private static void WriteConfig(string path, string jwtSecret, string token, int port)
    {
        var obj = new
        {
            jwtSecret,
            registrationToken = token,
            port
        };
        File.WriteAllText(path, JsonSerializer.Serialize(obj));
    }
}
