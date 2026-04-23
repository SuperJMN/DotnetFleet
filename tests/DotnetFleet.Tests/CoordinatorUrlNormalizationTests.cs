using DotnetFleet.Tool;
using DotnetFleet.WorkerService.Bootstrap;

namespace DotnetFleet.Tests;

public class CoordinatorUrlNormalizationTests
{
    [Theory]
    [InlineData("192.168.1.29:5000", "http://192.168.1.29:5000")]
    [InlineData("  192.168.1.29:5000  ", "http://192.168.1.29:5000")]
    [InlineData("host.local:5000", "http://host.local:5000")]
    [InlineData("http://192.168.1.29:5000", "http://192.168.1.29:5000")]
    [InlineData("http://192.168.1.29:5000/", "http://192.168.1.29:5000")]
    [InlineData("https://coord.example.com", "https://coord.example.com")]
    [InlineData("HTTP://Host:80", "http://host")]
    public void Resolver_Normalizes_BareHostPort(string input, string expected)
    {
        var ok = CoordinatorResolver.TryNormalizeCoordinatorUrl(input, out var normalized, out var error);
        Assert.True(ok, error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://host:5000")]
    [InlineData("not a url")]
    public void Resolver_Rejects_InvalidUrls(string input)
    {
        var ok = CoordinatorResolver.TryNormalizeCoordinatorUrl(input, out _, out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("192.168.1.29:5000", "http://192.168.1.29:5000")]
    [InlineData("http://host:5000/", "http://host:5000")]
    [InlineData("https://x.y.z", "https://x.y.z")]
    public void WorkerBootstrap_Normalizes_BareHostPort(string input, string expected)
    {
        var actual = WorkerBootstrap.NormalizeCoordinatorBaseUrl(input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://x")]
    [InlineData("::not-a-url::")]
    public void WorkerBootstrap_Throws_OnInvalidUrl(string? input)
    {
        Assert.Throws<InvalidOperationException>(() => WorkerBootstrap.NormalizeCoordinatorBaseUrl(input));
    }
}
