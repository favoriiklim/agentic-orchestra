using System.Text.Json.Serialization;

namespace AgenticOrchestra.Models;

/// <summary>
/// JSON payload sent FROM Layer 1 (Local Model) TO Layer 2 (Web Manager AI).
/// Contains the classified user task with project context.
/// </summary>
public sealed class ManagerTaskRequest
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("user_prompt")]
    public string UserPrompt { get; set; } = string.Empty;

    [JsonPropertyName("task_category")]
    public string TaskCategory { get; set; } = "general"; // "code", "research", "general", "debug"

    [JsonPropertyName("project_context")]
    public string ProjectContext { get; set; } = string.Empty;
}

/// <summary>
/// Compressed Telemetry JSON returned FROM Layer 2 (Web Manager AI) TO Layer 1 (Local Model).
/// Summarizes the entire orchestration cycle: what was done, who was spawned, outcomes.
/// </summary>
public sealed class ManagerTelemetry
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("task_executed")]
    public string TaskExecuted { get; set; } = string.Empty;

    [JsonPropertyName("workers_spawned")]
    public List<string> WorkersSpawned { get; set; } = new();

    [JsonPropertyName("final_outcome")]
    public string FinalOutcome { get; set; } = string.Empty;

    [JsonPropertyName("errors_handled")]
    public string ErrorsHandled { get; set; } = "None";

    [JsonPropertyName("retry_count")]
    public int RetryCount { get; set; } = 0;

    [JsonPropertyName("worker_reports")]
    public List<WorkerReport> WorkerReports { get; set; } = new();

    [JsonPropertyName("execution_time_seconds")]
    public double ExecutionTimeSeconds { get; set; }
}

/// <summary>
/// Individual report from a Layer 3 Worker Agent, collected by the Web Manager AI.
/// </summary>
public sealed class WorkerReport
{
    [JsonPropertyName("worker_name")]
    public string WorkerName { get; set; } = string.Empty;

    [JsonPropertyName("task")]
    public string Task { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "success"; // "success", "error", "timeout"
}
