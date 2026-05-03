using DotnetFleet.Core.Domain;

namespace DotnetFleet.Tests;

public class PackageBuildRequestTests
{
    [Fact]
    public void Serialize_round_trips_package_targets()
    {
        var request = new PackageBuildRequest
        {
            PackageProject = "src/App/App.csproj",
            Targets =
            [
                new PackageBuildTarget { Format = "exe-setup", Architecture = "x64" },
                new PackageBuildTarget { Format = "deb", Architecture = "arm64" }
            ]
        };

        var json = PackageBuildRequest.Serialize(request);
        var roundTrip = PackageBuildRequest.Deserialize(json);

        roundTrip.PackageProject.Should().Be("src/App/App.csproj");
        roundTrip.Targets.Should().HaveCount(2);
        roundTrip.Targets[0].ToDeployerTarget().Should().Be("exe-setup:x64");
        roundTrip.Targets[1].ToDeployerTarget().Should().Be("deb:arm64");
    }
}
