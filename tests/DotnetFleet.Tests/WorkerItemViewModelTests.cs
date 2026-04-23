using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using DotnetFleet.ViewModels;

namespace DotnetFleet.Tests;

public class WorkerItemViewModelTests
{
    private static FleetApiClient.WorkerInfo Info(int cpu, long mem, string? arch)
        => new(
            Id: Guid.NewGuid(),
            Name: "w",
            Status: WorkerStatus.Online,
            IsEmbedded: false,
            LastSeenAt: null,
            MaxDiskUsageGb: 10,
            RepoStoragePath: null,
            Version: null,
            ProcessorCount: cpu,
            TotalMemoryMb: mem,
            OperatingSystem: "Linux",
            Architecture: arch,
            CpuModel: null);

    [Fact]
    public void CapabilityLabel_renders_cores_memory_and_arch()
    {
        var vm = new WorkerItemViewModel(Info(8, 16 * 1024, "X64"), client: null!);
        vm.CapabilityLabel.Should().Be("8 core(s) · 16,0 GB · X64");
    }

    [Fact]
    public void CapabilityLabel_is_empty_when_no_capabilities_reported()
    {
        var vm = new WorkerItemViewModel(Info(0, 0, null), client: null!);
        vm.CapabilityLabel.Should().BeEmpty();
    }

    [Fact]
    public void CapabilityLabel_skips_missing_fields()
    {
        var vm = new WorkerItemViewModel(Info(4, 0, null), client: null!);
        vm.CapabilityLabel.Should().Be("4 core(s)");
    }
}
