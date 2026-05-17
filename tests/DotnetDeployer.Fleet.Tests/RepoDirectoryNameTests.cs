using DotnetDeployer.Fleet.WorkerService.RepoStorage;

namespace DotnetDeployer.Fleet.Tests;

public class RepoDirectoryNameTests
{
    [Theory]
    [InlineData("Pokémon Battle Engine", "Pokemon-Battle-Engine")]
    [InlineData("build worker:01", "build-worker_01")]
    [InlineData("   ", "project")]
    public void Create_ShouldProduceAsciiSafeDirectoryName(string name, string expected)
    {
        RepoDirectoryName.Create(name).Should().Be(expected);
    }
}
