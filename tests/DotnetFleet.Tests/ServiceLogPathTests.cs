using DotnetFleet.Coordinator;
using DotnetFleet.WorkerService;

namespace DotnetFleet.Tests;

public class ServiceLogPathTests
{
    [Fact]
    public void CoordinatorLogPath_UsesDataDirWhenProvided()
    {
        var path = CoordinatorHostBuilder.ResolveLogPath(@"C:\ProgramData\DotnetFleet\coordinator");

        path.Should().Be(Path.Combine(@"C:\ProgramData\DotnetFleet\coordinator", "logs", "coordinator-.log"));
    }

    [Fact]
    public void WorkerLogPath_UsesDataDirWhenProvided()
    {
        var path = WorkerHostBuilder.ResolveLogPath(@"C:\ProgramData\DotnetFleet\worker-build-01");

        path.Should().Be(Path.Combine(@"C:\ProgramData\DotnetFleet\worker-build-01", "logs", "worker-.log"));
    }
}
