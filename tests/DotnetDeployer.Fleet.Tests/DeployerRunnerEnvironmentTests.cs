using System.Diagnostics;
using DotnetDeployer.Fleet.WorkerService.Execution;

namespace DotnetDeployer.Fleet.Tests;

public class DeployerRunnerEnvironmentTests
{
    [Fact]
    public void ApplyBuildEnvironment_ShouldDisableCompilerAndNodeReuseForChildBuilds()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false
        };

        DeployerRunner.ApplyBuildEnvironment(psi);

        psi.Environment["UseSharedCompilation"].Should().Be("false");
        psi.Environment["MSBUILDDISABLENODEREUSE"].Should().Be("1");
    }
}
