using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Jobs;

/// <summary>Bounds for the database job log sink.</summary>
public sealed class JobLogOptions
{
    /// <summary>Events below this level never reach the database.</summary>
    public JobEventLevel MinLevel { get; set; } = JobEventLevel.Debug;

    /// <summary>Bounded buffer; a full channel drops events and counts them on the run.</summary>
    public int ChannelCapacity { get; set; } = 20_000;

    /// <summary>Burst window: queued events are written in one batch per interval.</summary>
    public int FlushIntervalSeconds { get; set; } = 1;
}
