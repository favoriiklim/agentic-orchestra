using System.Text.Json.Serialization;

namespace AgenticOrchestra.Models;

/// <summary>
/// Represents a single "dream" — the output of the DreamingService's sleep-mode analysis.
/// Created by analyzing accumulated ManagerTelemetry records to discover patterns,
/// recurring errors, and optimization opportunities.
/// </summary>
public sealed class DreamRecord
{
    [JsonPropertyName("dream_timestamp")]
    public DateTime DreamTimestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("patterns_discovered")]
    public List<string> PatternsDiscovered { get; set; } = new();

    [JsonPropertyName("recurring_errors")]
    public List<string> RecurringErrors { get; set; } = new();

    [JsonPropertyName("optimization_suggestions")]
    public List<string> OptimizationSuggestions { get; set; } = new();

    [JsonPropertyName("telemetries_analyzed")]
    public int TelemetriesAnalyzed { get; set; }

    [JsonPropertyName("consolidated_insight")]
    public string ConsolidatedInsight { get; set; } = string.Empty;
}
