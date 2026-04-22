namespace AgenticOrchestra.Models;

/// <summary>
/// Data model for persistent agent sessions.
/// </summary>
public sealed class SessionData
{
    /// <summary>
    /// A high-level description of what the project currently holds to avoid raw context dumping.
    /// </summary>
    public string ProjectStateSummary { get; set; } = "Initializing new project.";

    /// <summary>
    /// Sequential list of all structural actions performed by the Orchestrator.
    /// </summary>
    public List<AgentOperation> Operations { get; set; } = new();

    /// <summary>
    /// Accumulated Compressed Telemetry JSONs from the Web Manager AI.
    /// Used by DreamingService for sleep-mode pattern analysis.
    /// </summary>
    public List<ManagerTelemetry> TelemetryLog { get; set; } = new();

    /// <summary>
    /// History of dream analysis results from the DreamingService.
    /// The most recent entry is injected into the Manager's system prompt.
    /// </summary>
    public List<DreamRecord> DreamLog { get; set; } = new();
}

/// <summary>
/// Granular logging representation of an agent's input and output graph.
/// </summary>
public sealed class AgentOperation
{
    public string AgentName { get; set; } = string.Empty;
    public string TaskInstruction { get; set; } = string.Empty;
    public string OutputResult { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
