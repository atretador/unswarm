using Unswarm.Core.Models;

namespace Unswarm.Api.Dtos;

public sealed class SettingsResponse
{
    public int RequestTimeout { get; set; } = 120;
    public int HealthCheckInterval { get; set; } = 10;
    public bool AutoShutdownIdle { get; set; } = true;
    public int IdleTimeout { get; set; } = 300;
    public int LogRetention { get; set; } = 168;
    public bool EnableBenchmarking { get; set; } = true;
    public string PriorityMode { get; set; } = "fifo";
    public bool BatchDrain { get; set; }
    public bool LazyStop { get; set; } = true;
    public int MaxQueueDepth { get; set; } = 32;

    public int ParallelSlotSkipLimit { get; set; } = 3;

    public static SettingsResponse FromSettings(Settings s) => new()
    {
        RequestTimeout = s.RequestTimeout,
        HealthCheckInterval = s.HealthCheckInterval,
        AutoShutdownIdle = s.AutoShutdownIdle,
        IdleTimeout = s.IdleTimeout,
        LogRetention = s.LogRetention,
        EnableBenchmarking = s.EnableBenchmarking,
        PriorityMode = s.PriorityMode,
        BatchDrain = s.BatchDrain,
        LazyStop = s.LazyStop,
        MaxQueueDepth = s.MaxQueueDepth,
        ParallelSlotSkipLimit = s.ParallelSlotSkipLimit
    };
}

public sealed class SettingsUpdateRequest
{
    public int? RequestTimeout { get; set; }
    public int? HealthCheckInterval { get; set; }
    public bool? AutoShutdownIdle { get; set; }
    public int? IdleTimeout { get; set; }
    public int? LogRetention { get; set; }
    public bool? EnableBenchmarking { get; set; }
    public string? PriorityMode { get; set; }
    public bool? BatchDrain { get; set; }
    public bool? LazyStop { get; set; }
    public int? MaxQueueDepth { get; set; }

    public int? ParallelSlotSkipLimit { get; set; }
}
