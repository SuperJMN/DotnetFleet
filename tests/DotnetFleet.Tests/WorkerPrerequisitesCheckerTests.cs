using DotnetFleet.Tool;

namespace DotnetFleet.Tests;

public class WorkerPrerequisitesCheckerTests
{
    [Theory]
    [InlineData("Apt", "apt-get install -y llvm lld")]
    [InlineData("Dnf", "dnf install -y llvm lld")]
    [InlineData("Pacman", "pacman -S --noconfirm llvm lld")]
    public void InstallCommandFor_FormatsPerPackageManager(string pmName, string expected)
    {
        var pm = Enum.Parse<WorkerPrerequisitesChecker.PackageManager>(pmName);
        var cmd = WorkerPrerequisitesChecker.InstallCommandFor(pm, ["llvm", "lld"]);
        Assert.Equal(expected, cmd);
    }

    [Fact]
    public void ReportMissingDependencies_DoesNotThrow()
    {
        // Smoke-test: must never throw regardless of the host state.
        var ex = Record.Exception(WorkerPrerequisitesChecker.ReportMissingDependencies);
        Assert.Null(ex);
    }
}
