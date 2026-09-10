namespace Unswarm.Core.Models;

public sealed class AgentTelemetryData
{
    public HostMetrics? Host { get; set; }
    public List<GPUMetrics> Gpus { get; set; } = [];
    public Dictionary<string, ContainerMetrics> Containers { get; set; } = [];
    public DateTimeOffset CollectedAt { get; set; }
}

public sealed class HostMetrics
{
    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }
    public long RamUsedMb { get; set; }
    public long RamTotalMb { get; set; }
}

public sealed class GPUMetrics
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public double CorePercent { get; set; }
    public double MemoryPercent { get; set; }
    public long MemoryUsedMb { get; set; }
    public long MemoryTotalMb { get; set; }
}

public sealed class ContainerMetrics
{
    public string ContainerId { get; set; } = "";
    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }
    public long RamUsedMb { get; set; }
    public long RamTotalMb { get; set; }
}
