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
    private SessionData _currentSession;
    private string CurrentSessionFilePath => Path.Combine(_sessionDir, $"{_currentSession.Id}.json");

    /// <summary>
    /// For backward compatibility with existing agent manager initialization.
    /// </summary>
    public Task InitializeAsync() => Task.CompletedTask;

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
        _currentSession = new SessionData();
        Directory.CreateDirectory(_sessionDir);
    }

    /// <summary>
    /// Loads a specific session file from disk and maps it into memory.
    /// </summary>
    public async Task<SessionData> LoadSessionAsync(string filePath)
    {
        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                _currentSession = JsonSerializer.Deserialize<SessionData>(json, JsonOptions) ?? new SessionData();
            }
            catch
            {
                _currentSession = new SessionData();
            }
        }
        return _currentSession;
    }

    /// <summary>
    /// Returns a list of all saved session files, ordered newest first.
    /// </summary>
    public List<FileInfo> GetAvailableSessions()
    {
        Directory.CreateDirectory(_sessionDir);
        var dirInfo = new DirectoryInfo(_sessionDir);
        return dirInfo.GetFiles("session_*.json")
                      .OrderByDescending(f => f.CreationTimeUtc)
                      .ToList();
    }

    public SessionData GetCurrentSession() => _currentSession;

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
    /// Syncs the raw conversational history into the persistent session.
    /// </summary>
    public async Task UpdateRawHistoryAsync(List<ChatMessage> history)
    {
        // Deep copy the list so we aren't tied to the reference
        _currentSession.RawHistory = history.Select(h => new ChatMessage { Role = h.Role, Content = h.Content }).ToList();
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
        await File.WriteAllTextAsync(CurrentSessionFilePath, json);
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
