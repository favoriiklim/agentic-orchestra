using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticOrchestra.Models;

namespace AgenticOrchestra.Services;

/// <summary>
/// Persists the history of Swarm operations to a local JSON file.
/// Responsible for building the summarized 'Memory Context' string injected into the Head Manager.
/// Extended for the 3-layer hierarchy: stores ManagerTelemetry records and DreamRecords.
/// </summary>
public sealed class SessionLoggingService
{
    private readonly string _sessionDir;
    private readonly string _sessionFilePath;
    private readonly string _dreamDir;
    private SessionData _currentSession;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

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
        _dreamDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AgenticOrchestra", "dreams");
        _currentSession = new SessionData();
    }

    /// <summary>
    /// Loads the past session from disk if it exists.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            Directory.CreateDirectory(_sessionDir);
            Directory.CreateDirectory(_dreamDir);

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
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Logs a complete interaction and asynchronously saves it to disk.
    /// </summary>
    public async Task AddOperationAsync(string agentName, string instruction, string output)
    {
        await _semaphore.WaitAsync();
        try
        {
            _currentSession.Operations.Add(new AgentOperation
            {
                AgentName = agentName,
                TaskInstruction = instruction,
                OutputResult = output
            });
            
            await SaveSessionAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Telemetry Persistence (3-Layer Hierarchy) ────────────────────

    /// <summary>
    /// Persists a ManagerTelemetry record from a completed orchestration cycle.
    /// These accumulate and are later analyzed by the DreamingService.
    /// </summary>
    public async Task AddTelemetryAsync(ManagerTelemetry telemetry)
    {
        await _semaphore.WaitAsync();
        try
        {
            _currentSession.TelemetryLog.Add(telemetry);
            await SaveSessionAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Returns the most recent N telemetry records for DreamingService analysis.
    /// </summary>
    public List<ManagerTelemetry> GetRecentTelemetries(int count)
    {
        // For reads, we lock briefly to ensure the list is not modified during counting/enumerable
        _semaphore.Wait();
        try
        {
            return _currentSession.TelemetryLog
                .TakeLast(count)
                .ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Returns the total count of unanalyzed telemetries since the last dream.
    /// </summary>
    public int GetTelemetryCount() 
    {
        _semaphore.Wait();
        try
        {
            return _currentSession.TelemetryLog.Count;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Dream Persistence ────────────────────────────────────────────

    /// <summary>
    /// Saves a DreamRecord both to the session and as a standalone timestamped file.
    /// </summary>
    public async Task SaveDreamRecordAsync(DreamRecord dream)
    {
        await _semaphore.WaitAsync();
        try
        {
            _currentSession.DreamLog.Add(dream);
            await SaveSessionAsync();

            // Also save as standalone file for archival
            var dreamFileName = $"dream_{dream.DreamTimestamp:yyyyMMdd_HHmmss}.json";
            var dreamFilePath = Path.Combine(_dreamDir, dreamFileName);
            var dreamJson = JsonSerializer.Serialize(dream, JsonOptions);
            await File.WriteAllTextAsync(dreamFilePath, dreamJson);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Returns the consolidated insight from the most recent dream, if any.
    /// Used for memory injection into the Manager's system prompt.
    /// </summary>
    public string? GetLatestDreamInsight()
    {
        _semaphore.Wait();
        try
        {
            var latestDream = _currentSession.DreamLog.LastOrDefault();
            return latestDream?.ConsolidatedInsight;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Updates the global high-level summary of the project state.
    /// </summary>
    public async Task UpdateProjectStateAsync(string newSummary)
    {
        await _semaphore.WaitAsync();
        try
        {
            _currentSession.ProjectStateSummary = newSummary;
            await SaveSessionAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task SaveSessionAsync()
    {
        var json = JsonSerializer.Serialize(_currentSession, JsonOptions);
        await File.WriteAllTextAsync(_sessionFilePath, json);
    }

    /// <summary>
    /// Constructs the Memory Injection blob.
    /// Wraps the global summary alongside the raw data of the last 5 operations,
    /// plus any dream insights from previous sessions.
    /// </summary>
    public string GetMemoryInjectionString()
    {
        _semaphore.Wait();
        try
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

            // ── Dream Insights Injection ──
            var latestDream = _currentSession.DreamLog.LastOrDefault();
            var dreamInsight = latestDream?.ConsolidatedInsight;
            if (!string.IsNullOrWhiteSpace(dreamInsight))
            {
                stringBuilder.AppendLine();
                stringBuilder.AppendLine("### [SYSTEM MEMORY: DREAM ANALYSIS INSIGHTS] ###");
                stringBuilder.AppendLine("The following insights were learned from analyzing your past task execution patterns:");
                stringBuilder.AppendLine(dreamInsight);
            }

            return stringBuilder.ToString();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Re-initializes the session data, wiping memory.
    /// </summary>
    public async Task ClearSessionAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            _currentSession = new SessionData();
            await SaveSessionAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
