using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;

namespace DotnetFleet.Tests;

public class CapabilityWorkerSelectorTests
{
    private readonly IWorkerSelector selector = new CapabilityWorkerSelector();

    private static Worker Make(
        string name,
        int cpu = 0,
        long memMb = 0,
        string? arch = null,
        WorkerStatus status = WorkerStatus.Online,
        Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            ProcessorCount = cpu,
            TotalMemoryMb = memMb,
            Architecture = arch,
            Status = status
        };

    [Fact]
    public void Score_uses_cores_memory_and_architecture_bonus()
    {
        // 8 cores * 10 + (16384/1024) * 5 + 3 (x64) = 80 + 80 + 3 = 163
        var pc = Make("pc", cpu: 8, memMb: 16 * 1024, arch: "X64");
        selector.Score(pc).Should().Be(163);

        // RPi4: 4 * 10 + (4096/1024) * 5 + 1 (arm64) = 40 + 20 + 1 = 61
        var rpi = Make("rpi", cpu: 4, memMb: 4 * 1024, arch: "Arm64");
        selector.Score(rpi).Should().Be(61);
    }

    [Fact]
    public void Score_is_zero_for_worker_with_no_reported_capabilities()
    {
        selector.Score(Make("legacy")).Should().Be(0);
    }

    [Fact]
    public void Score_treats_negative_values_as_zero()
    {
        var weird = Make("weird", cpu: -8, memMb: -1024, arch: "X64");
        selector.Score(weird).Should().Be(3); // only the arch bonus survives
    }

    [Fact]
    public void Score_arch_bonus_is_case_insensitive_and_handles_aliases()
    {
        selector.Score(Make("a", arch: "x64")).Should().Be(3);
        selector.Score(Make("a", arch: "AMD64")).Should().Be(3);
        selector.Score(Make("a", arch: "arm64")).Should().Be(1);
        selector.Score(Make("a", arch: "AArch64")).Should().Be(1);
        selector.Score(Make("a", arch: "Arm")).Should().Be(0);
        selector.Score(Make("a", arch: null)).Should().Be(0);
    }

    [Fact]
    public void SelectBest_picks_PC_over_RPi4()
    {
        var rpi = Make("rpi4", cpu: 4, memMb: 4 * 1024, arch: "Arm64");
        var pc = Make("pc", cpu: 8, memMb: 16 * 1024, arch: "X64");

        selector.SelectBest(new[] { rpi, pc })!.Name.Should().Be("pc");
        selector.SelectBest(new[] { pc, rpi })!.Name.Should().Be("pc");
    }

    [Fact]
    public void SelectBest_picks_high_spec_PC_over_low_spec_PC()
    {
        var low = Make("low", cpu: 2, memMb: 4 * 1024, arch: "X64");
        var high = Make("high", cpu: 16, memMb: 64 * 1024, arch: "X64");

        selector.SelectBest(new[] { low, high })!.Name.Should().Be("high");
    }

    [Fact]
    public void SelectBest_breaks_ties_deterministically_by_name_then_id()
    {
        var idA = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var idB = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var alpha = Make("beta", cpu: 4, memMb: 8 * 1024, arch: "X64", id: idA);
        var beta = Make("alpha", cpu: 4, memMb: 8 * 1024, arch: "X64", id: idB);

        // Same score, ordinal name compare → "alpha" wins regardless of input order.
        selector.SelectBest(new[] { alpha, beta })!.Name.Should().Be("alpha");
        selector.SelectBest(new[] { beta, alpha })!.Name.Should().Be("alpha");

        // Same score and same name → smallest Id wins.
        var twin1 = Make("twin", cpu: 4, memMb: 8 * 1024, arch: "X64", id: idA);
        var twin2 = Make("twin", cpu: 4, memMb: 8 * 1024, arch: "X64", id: idB);
        selector.SelectBest(new[] { twin2, twin1 })!.Id.Should().Be(idA);
    }

    [Fact]
    public void SelectBest_returns_the_only_worker_when_alone()
    {
        var only = Make("solo", cpu: 4, memMb: 8 * 1024, arch: "X64");
        selector.SelectBest(new[] { only })!.Id.Should().Be(only.Id);
    }

    [Fact]
    public void SelectBest_returns_null_when_no_candidates()
    {
        selector.SelectBest(Array.Empty<Worker>()).Should().BeNull();
    }

    [Fact]
    public void Worker_with_capabilities_beats_legacy_worker_without_them()
    {
        var legacy = Make("legacy");
        var modern = Make("modern", cpu: 1, memMb: 1024, arch: "X64");

        selector.SelectBest(new[] { legacy, modern })!.Name.Should().Be("modern");
    }
}
