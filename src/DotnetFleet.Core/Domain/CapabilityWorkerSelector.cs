using DotnetFleet.Core.Interfaces;

namespace DotnetFleet.Core.Domain;

/// <summary>
/// Default capability-aware <see cref="IWorkerSelector"/>. Scores a worker by combining
/// CPU cores, RAM and a small bonus for desktop-class architectures so that, given a
/// choice, beefier hardware always wins. The formula is intentionally simple and
/// deterministic — it minimizes deployment time on heterogeneous fleets (e.g. RPi vs PC)
/// without needing per-worker benchmarks.
/// </summary>
/// <remarks>
/// Score formula: <c>cores * 10 + (memoryMb / 1024) * 5 + archBonus</c>.
/// <list type="bullet">
///   <item>Arch bonus: x64 = 3, arm64 = 1, arm/unknown = 0.</item>
///   <item>Workers with 0 cores AND 0 memory (legacy / not yet reported) score 0.</item>
///   <item>Tiebreak: <see cref="Worker.Name"/> ordinal, then <see cref="Worker.Id"/>.</item>
/// </list>
/// </remarks>
public class CapabilityWorkerSelector : IWorkerSelector
{
    public int Score(Worker worker)
    {
        var cores = Math.Max(0, worker.ProcessorCount);
        var memoryGb = (int)(Math.Max(0, worker.TotalMemoryMb) / 1024);
        var archBonus = (worker.Architecture ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "x64" or "amd64" => 3,
            "arm64" or "aarch64" => 1,
            _ => 0
        };
        return cores * 10 + memoryGb * 5 + archBonus;
    }

    public Worker? SelectBest(IEnumerable<Worker> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .OrderByDescending(Score)
            .ThenBy(w => w.Name, StringComparer.Ordinal)
            .ThenBy(w => w.Id)
            .FirstOrDefault();
    }
}
