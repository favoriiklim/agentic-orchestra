namespace AgenticOrchestra.Models;

/// <summary>
/// Data model for persistent agent sessions.
/// </summary>
public sealed class SessionData
{
    public string Id { get; set; } = $"session_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// A high-level description of what the project currently holds to avoid raw context dumping.
    /// </summary>
    public string ProjectStateSummary { get; set; } = "Initializing new project.";

    /// <summary>
    /// Sequential list of all structural actions performed by the Orchestrator.
    /// </summary>
    public List<AgentOperation> Operations { get; set; } = new();

    /// <summary>
    /// Deep copy of the actual LLM turn history to resume context exactly. 
    /// </summary>
    public List<ChatMessage> RawHistory { get; set; } = new();
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
