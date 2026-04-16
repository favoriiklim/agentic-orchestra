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
