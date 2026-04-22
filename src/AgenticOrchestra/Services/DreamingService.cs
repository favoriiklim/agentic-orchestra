using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticOrchestra.Models;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// Sleep Mode Learning Engine — "The Dreaming Service".
/// Analyzes accumulated ManagerTelemetry records to discover patterns,
/// recurring errors, and optimization opportunities. Results are persisted
/// as DreamRecords and injected into future sessions as learned knowledge.
///
/// Dream analysis runs in an isolated "DreamAnalyzer" tab, completely separate
/// from the live Manager conversation context.
///
/// Triggered either:
///   1. Manually via the --dream CLI command (primary)
///   2. Automatically when telemetry count exceeds threshold (if AutoDreamEnabled)
///   3. On session exit (if AutoDreamOnExit is enabled)
/// </summary>
public sealed class DreamingService
{
    private readonly PlaywrightWebAgent _webAgent;
    private readonly SessionLoggingService _sessionLogger;
    private readonly AppConfig _config;

    private const string DreamAgentTab = "DreamAnalyzer";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DreamingService(PlaywrightWebAgent webAgent, SessionLoggingService sessionLogger, AppConfig config)
    {
        _webAgent = webAgent;
        _sessionLogger = sessionLogger;
        _config = config;
    }

    /// <summary>
    /// Checks if the telemetry threshold has been met and triggers a dream cycle if so.
    /// Reads the persisted threshold marker to avoid re-analyzing on restart.
    /// </summary>
    public async Task<bool> CheckAndDreamIfNeededAsync(CancellationToken ct = default)
    {
        int totalCount = await _sessionLogger.GetTelemetryCountAsync();
        int lastDreamCount = await _sessionLogger.GetLastDreamTelemetryCountAsync();

        if (totalCount - lastDreamCount >= _config.Dreaming.TelemetryThreshold)
        {
            AnsiConsole.MarkupLine($"\n[bold mediumpurple3]💤 Telemetry threshold ({_config.Dreaming.TelemetryThreshold}) reached. Initiating Dream Cycle...[/]");
            await RunDreamCycleAsync(ct);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Executes a full dream analysis cycle in an isolated DreamAnalyzer tab.
    /// The Manager tab and live user conversation are never touched.
    /// Failures are caught and logged without propagating to the caller.
    /// </summary>
    public async Task<DreamRecord> RunDreamCycleAsync(CancellationToken ct = default)
    {
        var telemetries = await _sessionLogger.GetRecentTelemetriesAsync(50);

        if (telemetries.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim yellow]No telemetries to analyze. Dream cycle skipped.[/]");
            return new DreamRecord
            {
                ConsolidatedInsight = "No telemetries available for analysis.",
                TelemetriesAnalyzed = 0
            };
        }

        var telemetrySummary = BuildTelemetrySummary(telemetries);

        var dreamPrompt = $@"### DREAM ANALYSIS MODE ###
You are entering Sleep Mode Learning. Your task is to analyze the following execution telemetries from your recent work sessions and extract meta-patterns.

ANALYZE THE FOLLOWING {telemetries.Count} TELEMETRY RECORDS:
{telemetrySummary}

RESPOND WITH EXACTLY THIS JSON STRUCTURE (no markdown fences, no extra text):
{{
  ""patterns_discovered"": [""pattern1"", ""pattern2""],
  ""recurring_errors"": [""error1"", ""error2""],
  ""optimization_suggestions"": [""suggestion1"", ""suggestion2""],
  ""consolidated_insight"": ""A 2-3 sentence synthesis of what you learned about how to work more effectively.""
}}

Focus on:
1. Task types that succeeded vs failed — what made the difference?
2. Which worker combinations worked best together?
3. Common error patterns and how they were (or should be) resolved.
4. Workflow optimizations for future tasks.";

        DreamRecord dream;

        try
        {
            string dreamResponse = "";
            await AnsiConsole.Status()
                .SpinnerStyle(Style.Parse("mediumpurple3"))
                .StartAsync("💤 Dreaming... Analyzing execution patterns...", async ctx =>
                {
                    // Use isolated DreamAnalyzer tab — never the live Manager tab
                    dreamResponse = await _webAgent.SendMessageAsync(DreamAgentTab, dreamPrompt, ct);
                });

            dream = ParseDreamResponse(dreamResponse, telemetries.Count);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[dim yellow]Dream cycle cancelled.[/]");
            dream = new DreamRecord
            {
                ConsolidatedInsight = "Dream analysis was cancelled by user.",
                TelemetriesAnalyzed = telemetries.Count
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Dream cycle error:[/] {Markup.Escape(ex.Message)}");
            dream = new DreamRecord
            {
                ConsolidatedInsight = $"Dream analysis failed: {ex.Message}",
                TelemetriesAnalyzed = telemetries.Count
            };
        }
        finally
        {
            // Always close the DreamAnalyzer tab to free resources
            try { await _webAgent.CloseWorkerTabAsync(DreamAgentTab); } catch { /* best-effort cleanup */ }
        }

        // Persist the dream and update threshold marker
        await _sessionLogger.SaveDreamRecordAsync(dream);
        int currentCount = await _sessionLogger.GetTelemetryCountAsync();
        await _sessionLogger.SetLastDreamTelemetryCountAsync(currentCount);

        AnsiConsole.MarkupLine("[bold mediumpurple3]💤 Dream cycle complete. Insights stored for next session.[/]");
        DisplayDreamSummary(dream);

        return dream;
    }

    /// <summary>
    /// Builds a condensed text summary of telemetry records for the dream prompt.
    /// </summary>
    private static string BuildTelemetrySummary(List<ManagerTelemetry> telemetries)
    {
        var sb = new System.Text.StringBuilder();
        int index = 1;

        foreach (var t in telemetries)
        {
            sb.AppendLine($"--- Telemetry #{index++} (ID: {t.TaskId}) ---");
            sb.AppendLine($"Task: {t.TaskExecuted}");
            sb.AppendLine($"Workers: [{string.Join(", ", t.WorkersSpawned)}]");
            sb.AppendLine($"Outcome: {t.FinalOutcome}");
            sb.AppendLine($"Errors: {t.ErrorsHandled}");
            sb.AppendLine($"Retries: {t.RetryCount}");
            sb.AppendLine($"Time: {t.ExecutionTimeSeconds:F1}s");

            if (t.WorkerReports.Count > 0)
            {
                foreach (var wr in t.WorkerReports)
                {
                    sb.AppendLine($"  Worker [{wr.WorkerName}]: {wr.Status} — {wr.Task}");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses the Manager's dream analysis response into a structured DreamRecord.
    /// Falls back to storing the raw response as insight if JSON parsing fails.
    /// </summary>
    private static DreamRecord ParseDreamResponse(string response, int telemetriesAnalyzed)
    {
        var jsonContent = response.Trim();

        // Strip markdown code fences if present
        if (jsonContent.StartsWith("```"))
        {
            var firstNewline = jsonContent.IndexOf('\n');
            if (firstNewline > 0) jsonContent = jsonContent[(firstNewline + 1)..];
            if (jsonContent.EndsWith("```"))
                jsonContent = jsonContent[..^3];
            jsonContent = jsonContent.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            return new DreamRecord
            {
                TelemetriesAnalyzed = telemetriesAnalyzed,
                PatternsDiscovered = ExtractStringList(root, "patterns_discovered"),
                RecurringErrors = ExtractStringList(root, "recurring_errors"),
                OptimizationSuggestions = ExtractStringList(root, "optimization_suggestions"),
                ConsolidatedInsight = root.TryGetProperty("consolidated_insight", out var insight)
                    ? insight.GetString() ?? ""
                    : ""
            };
        }
        catch
        {
            return new DreamRecord
            {
                TelemetriesAnalyzed = telemetriesAnalyzed,
                ConsolidatedInsight = response
            };
        }
    }

    private static List<string> ExtractStringList(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop)) return new();
        if (prop.ValueKind != JsonValueKind.Array) return new();

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            var val = item.GetString();
            if (!string.IsNullOrWhiteSpace(val)) list.Add(val);
        }
        return list;
    }

    /// <summary>
    /// Renders a concise dream summary to the console.
    /// </summary>
    private static void DisplayDreamSummary(DreamRecord dream)
    {
        AnsiConsole.WriteLine();
        var panel = new Panel(new Markup(Markup.Escape(dream.ConsolidatedInsight)))
        {
            Border = BoxBorder.Double,
            Padding = new Padding(1, 1, 1, 1),
            Header = new PanelHeader($" 💤 Dream Insight ({dream.TelemetriesAnalyzed} telemetries analyzed) ", Justify.Left)
        };
        panel.BorderColor(Color.MediumPurple3);
        AnsiConsole.Write(panel);

        if (dream.PatternsDiscovered.Count > 0)
        {
            AnsiConsole.MarkupLine("[dim]Patterns:[/]");
            foreach (var p in dream.PatternsDiscovered)
                AnsiConsole.MarkupLine($"  [mediumpurple3]•[/] {Markup.Escape(p)}");
        }

        if (dream.RecurringErrors.Count > 0)
        {
            AnsiConsole.MarkupLine("[dim]Recurring Errors:[/]");
            foreach (var e in dream.RecurringErrors)
                AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(e)}");
        }

        if (dream.OptimizationSuggestions.Count > 0)
        {
            AnsiConsole.MarkupLine("[dim]Suggestions:[/]");
            foreach (var s in dream.OptimizationSuggestions)
                AnsiConsole.MarkupLine($"  [green]•[/] {Markup.Escape(s)}");
        }

        AnsiConsole.WriteLine();
    }
}
