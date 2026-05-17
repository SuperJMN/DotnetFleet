using DotnetDeployer.Fleet.Tooling;

namespace DotnetDeployer.Fleet.Tests;

public class ToolIdentityTests
{
    [Theory]
    [InlineData("/home/jmn/.nuget/packages/dotnetdeployer.fleet.tool/0.2.0/tools/net10.0/any/fleet", "DotnetDeployer.Fleet.Tool", "0.2.0")]
    [InlineData("/home/jmn/.nuget/packages/dotnetfleet.tool/0.1.15/tools/net10.0/any/fleet", "DotnetFleet.Tool", "0.1.15")]
    [InlineData(@"C:\Users\jmn\.nuget\packages\dotnetdeployer.fleet.tool\1.0.0\tools\net10.0\any\fleet.exe", "DotnetDeployer.Fleet.Tool", "1.0.0")]
    public void ExtractsPackageIdAndVersionFromNugetToolPath(string path, string packageId, string version)
    {
        ToolIdentity.ExtractPackageIdFromPath(path).Should().Be(packageId);
        ToolIdentity.ExtractVersionFromPath(path).Should().Be(version);
    }

    [Fact]
    public void FindsInstalledKnownPackageIdFromDotnetToolList()
    {
        const string output = """
            Package Id                      Version      Commands
            -----------------------------------------------------
            dotnetdeployer.fleet.tool       0.2.0        fleet
            """;

        ToolIdentity.FindInstalledKnownPackageId(output).Should().Be("DotnetDeployer.Fleet.Tool");
        ToolIdentity.FindInstalledVersion(output, "DotnetDeployer.Fleet.Tool").Should().Be("0.2.0");
    }
}
