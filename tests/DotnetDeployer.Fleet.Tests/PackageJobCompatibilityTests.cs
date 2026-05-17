using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Tests;

public class PackageJobCompatibilityTests
{
    [Fact]
    public void Package_build_jobs_do_not_filter_by_worker_operating_system()
    {
        var job = new DeploymentJob
        {
            Kind = JobKind.PackageBuild,
            PackageRequestJson = PackageBuildRequest.Serialize(new PackageBuildRequest
            {
                Targets =
                [
                    new PackageBuildTarget { Format = "exe-setup", Architecture = "x64" }
                ]
            })
        };

        var linuxWorker = new Worker
        {
            Name = "linux",
            Status = WorkerStatus.Online,
            OperatingSystem = "Linux",
            Architecture = "Arm64"
        };

        JobAssignmentService.IsCompatible(job, linuxWorker).Should().BeTrue();
    }
}
