using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticOrchestra.Models;

namespace AgenticOrchestra.Services;

/// <summary>
/// Persists the history of Swarm operations to a local JSON file.
/// Responsible for building the summarized 'Memory Context' string injected into the Head Manager.
/// </summary>
public sealed class SessionLoggingService
{
    private readonly string _sessionDir;
    private readonly string _sessionFilePath;
    private SessionData _currentSession;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SessionLoggingService()
    {
        _sessionDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AgenticOrchestra", "sessions");
        _sessionFilePath = Path.Combine(_sessionDir, "latest_session.json");
        _currentSession = new SessionData();
    }

    /// <summary>
    /// Loads the past session from disk if it exists.
    /// </summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_sessionDir);
        if (File.Exists(_sessionFilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_sessionFilePath);
                _currentSession = JsonSerializer.Deserialize<SessionData>(json, JsonOptions) ?? new SessionData();
            }
            catch
            {
                // If corrupted, just overwrite with a fresh session
                _currentSession = new SessionData();
            }
        }
    }

    /// <summary>
    /// Logs a complete interaction and asynchronously saves it to disk.
    /// </summary>
    public async Task AddOperationAsync(string agentName, string instruction, string output)
    {
        _currentSession.Operations.Add(new AgentOperation
        {
            AgentName = agentName,
            TaskInstruction = instruction,
            OutputResult = output
        });
        
        await SaveSessionAsync();
    }

    /// <summary>
    /// Updates the global high-level summary of the project state.
    /// </summary>
    public async Task UpdateProjectStateAsync(string newSummary)
    {
        _currentSession.ProjectStateSummary = newSummary;
        await SaveSessionAsync();
    }

    private async Task SaveSessionAsync()
    {
        var json = JsonSerializer.Serialize(_currentSession, JsonOptions);
        await File.WriteAllTextAsync(_sessionFilePath, json);
    }

    /// <summary>
    /// Constructs the Memory Injection blob.
    /// Wraps the global summary alongside the raw data of the last 5 operations.
    /// </summary>
    public string GetMemoryInjectionString()
    {
        var stringBuilder = new System.Text.StringBuilder();
        stringBuilder.AppendLine("### [SYSTEM MEMORY: EXISTING PROJECT STATE] ###");
        stringBuilder.AppendLine(_currentSession.ProjectStateSummary);
        stringBuilder.AppendLine();
        
        var recentOps = _currentSession.Operations.TakeLast(5).ToList();
        if (recentOps.Count > 0)
        {
            stringBuilder.AppendLine("### [SYSTEM MEMORY: LAST 5 OPERATIONS] ###");
            foreach (var op in recentOps)
            {
                stringBuilder.AppendLine($"[AGENT: {op.AgentName}]");
                stringBuilder.AppendLine($"TASK: {op.TaskInstruction}");
                stringBuilder.AppendLine($"RESULT: {op.OutputResult}");
                stringBuilder.AppendLine("---");
            }
        }
        else
        {
            stringBuilder.AppendLine("No preceding operations exist in the current memory bank.");
        }

        return stringBuilder.ToString();
    }

    /// <summary>
    /// Re-initializes the session data, wiping memory.
    /// </summary>
    public async Task ClearSessionAsync()
    {
        _currentSession = new SessionData();
        await SaveSessionAsync();
    }
}
