using System.Runtime.InteropServices;

namespace DotnetFleet.WorkerService.Bootstrap;

/// <summary>
/// Reports the host machine's hardware capabilities to the coordinator so that the
/// capability-aware worker selector can score this worker against its peers.
/// All values are best-effort: when a metric can't be determined the corresponding
/// field is left at its default (0 / null), which the coordinator interprets as
/// "least preferred but still selectable".
/// </summary>
public static class WorkerCapabilityProbe
{
    public record Capabilities(
        int ProcessorCount,
        long TotalMemoryMb,
        string OperatingSystem,
        string Architecture,
        string? CpuModel);

    public static Capabilities Detect()
    {
        var os = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "OSX"
            : RuntimeInformation.OSDescription;

        var arch = RuntimeInformation.OSArchitecture.ToString();

        long memoryMb = 0;
        try
        {
            var info = GC.GetGCMemoryInfo();
            // TotalAvailableMemoryBytes reflects the container/host memory limit
            // visible to the process — good enough as a coarse capacity signal.
            memoryMb = Math.Max(0, info.TotalAvailableMemoryBytes / (1024 * 1024));
        }
        catch
        {
            memoryMb = 0;
        }

        return new Capabilities(
            ProcessorCount: Environment.ProcessorCount,
            TotalMemoryMb: memoryMb,
            OperatingSystem: os,
            Architecture: arch,
            CpuModel: null);
    }
}
