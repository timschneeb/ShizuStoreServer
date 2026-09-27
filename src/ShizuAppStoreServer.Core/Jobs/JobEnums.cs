namespace ShizuAppStoreServer.Core.Jobs;

/// <summary>What kind of background job a run belongs to.</summary>
public enum JobKind
{
    Sync,
    IconRefresh,
    ScreenshotRefresh,
    UsageAnalysis,
}

/// <summary>Why a run started.</summary>
public enum JobTrigger
{
    Startup,
    Scheduled,
    Nightly,
    Webhook,
    Manual,
    Cli,
    Backfill,
    Auto,
}

/// <summary>Lifecycle state of a run.</summary>
public enum JobStatus
{
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
    Interrupted,
}

/// <summary>Severity of one event inside a run.</summary>
public enum JobEventLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Machine readable event category, used for filtering in the viewer.</summary>
public enum JobEventType
{
    Phase,
    App,
    Decision,
    Download,
    Analyze,
    Release,
    Render,
    Poll,
    Issue,
    AiSnapshot,
    AiModelCall,
    AiToolCall,
    AiValidation,
    AiResult,
    Error,
}
