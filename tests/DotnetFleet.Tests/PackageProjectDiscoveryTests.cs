using DotnetFleet.Coordinator.Services;

namespace DotnetFleet.Tests;

public class PackageProjectDiscoveryTests
{
    [Fact]
    public void ReadProjectsFromYaml_reads_github_package_projects()
    {
        const string yaml = """
            github:
              packages:
                - project: src/App/App.csproj
                  formats:
                    - type: exe-setup
                - project: src/Tool/Tool.csproj
                  formats:
                    - type: deb
            """;

        var projects = PackageProjectDiscovery.ReadProjectsFromYaml(yaml);

        projects.Should().Equal("src/App/App.csproj", "src/Tool/Tool.csproj");
    }

    [Fact]
    public void ReadProjectsFromYaml_returns_empty_when_no_packages_are_configured()
    {
        const string yaml = """
            github:
              enabled: true
            """;

        var projects = PackageProjectDiscovery.ReadProjectsFromYaml(yaml);

        projects.Should().BeEmpty();
    }
}
