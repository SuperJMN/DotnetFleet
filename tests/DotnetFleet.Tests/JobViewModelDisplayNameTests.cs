using DotnetFleet.Core.Domain;
using DotnetFleet.ViewModels;

namespace DotnetFleet.Tests;

/// <summary>
/// Verifies that the deployment list ViewModel projects a friendly name based on the
/// underlying <see cref="DeploymentJob.Version"/> and reacts to updates pushed in via
/// <see cref="JobViewModel.ApplyJobUpdate"/>.
/// </summary>
public class JobViewModelDisplayNameTests
{
    private static JobViewModel BuildVm(DeploymentJob job)
    {
        // The non-null parameters are only consumed by the Open() command which we never
        // invoke in these tests, so passing null! is safe here.
        return new JobViewModel(job, client: null!, navigator: null!, projectDetail: null!);
    }

    [Fact]
    public void Falls_back_to_short_GUID_when_no_version_is_known_yet()
    {
        var job = new DeploymentJob { Id = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789") };

        var vm = BuildVm(job);

        vm.DisplayName.Should().Be("abcdef01");
    }

    [Fact]
    public void Initial_version_on_the_underlying_job_drives_the_display_name()
    {
        var job = new DeploymentJob { Id = Guid.NewGuid(), Version = "2.0.1" };

        var vm = BuildVm(job);

        vm.DisplayName.Should().Be("2.0.1");
        vm.Version.Should().Be("2.0.1");
    }

    [Fact]
    public void DisplayName_updates_when_underlying_deployment_is_renamed()
    {
        var job = new DeploymentJob { Id = Guid.NewGuid() };
        var vm = BuildVm(job);

        var seen = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobViewModel.DisplayName))
                seen.Add(vm.DisplayName);
        };

        var renamed = new DeploymentJob { Id = job.Id, Version = "3.4.5-rc.1" };
        vm.ApplyJobUpdate(renamed);

        vm.DisplayName.Should().Be("3.4.5-rc.1");
        vm.Version.Should().Be("3.4.5-rc.1");
        seen.Should().Contain("3.4.5-rc.1");
    }

    [Fact]
    public void Updates_for_a_different_job_id_are_ignored()
    {
        var job = new DeploymentJob { Id = Guid.NewGuid(), Version = "1.0.0" };
        var vm = BuildVm(job);

        vm.ApplyJobUpdate(new DeploymentJob { Id = Guid.NewGuid(), Version = "9.9.9" });

        vm.Version.Should().Be("1.0.0");
        vm.DisplayName.Should().Be("1.0.0");
    }
}
