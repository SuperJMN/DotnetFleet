using DotnetDeployer.Fleet.Tooling;

namespace DotnetDeployer.Fleet.Tests;

public class SudoElevationTests
{
    [Fact]
    public void BuildWindowsElevatedProcess_WhenRunningUnderDotnet_ShouldPassDllAndUserArguments()
    {
        var process = SudoElevation.BuildWindowsElevatedProcess(
            @"C:\Program Files\dotnet\dotnet.exe",
            [
                @"C:\Tools With Space\DotnetFleet.Tool.dll",
                "coordinator",
                "install",
                "--data-dir",
                @"C:\ProgramData\DotnetFleet\coordinator data"
            ],
            @"C:\Users\JMN\Repos\DotnetFleet");

        process.FileName.Should().Be(@"C:\Program Files\dotnet\dotnet.exe");
        process.Verb.Should().Be("runas");
        process.UseShellExecute.Should().BeTrue();
        process.WorkingDirectory.Should().Be(@"C:\Users\JMN\Repos\DotnetFleet");
        process.Arguments.Should().Be(
            "\"C:\\Tools With Space\\DotnetFleet.Tool.dll\" coordinator install --data-dir \"C:\\ProgramData\\DotnetFleet\\coordinator data\"");
    }

    [Fact]
    public void BuildWindowsElevatedProcess_WhenRunningAsAppHost_ShouldPassOnlyUserArguments()
    {
        var process = SudoElevation.BuildWindowsElevatedProcess(
            @"C:\ProgramData\DotnetFleet\tools\fleet.exe",
            [
                @"C:\ProgramData\DotnetFleet\tools\DotnetFleet.Tool.dll",
                "worker",
                "install",
                "--name",
                "build worker"
            ],
            @"C:\Users\JMN\Repos\DotnetFleet");

        process.FileName.Should().Be(@"C:\ProgramData\DotnetFleet\tools\fleet.exe");
        process.Arguments.Should().Be("worker install --name \"build worker\"");
    }
}
