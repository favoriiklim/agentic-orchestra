using System.Text.Json.Serialization;

namespace AgenticOrchestra.Models;

/// <summary>
/// Strict JSON schema for Agent 1 (The Critic / Lead Reviewer) output.
/// The Critic must output exactly this format to approve or reject work.
/// </summary>
public sealed class SquadReviewResult
{
    /// <summary>"APPROVED" or "REJECTED"</summary>
    [JsonPropertyName("Status")]
    public string Status { get; set; } = "APPROVED";

    /// <summary>Which agent's work is being targeted: "Innovator" or "Implementer"</summary>
    [JsonPropertyName("Target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>If REJECTED, the correction instructions to route back to the target agent.</summary>
    [JsonPropertyName("CorrectionPrompt")]
    public string CorrectionPrompt { get; set; } = string.Empty;

    public bool IsApproved => Status.Equals("APPROVED", StringComparison.OrdinalIgnoreCase);
    public bool IsRejected => Status.Equals("REJECTED", StringComparison.OrdinalIgnoreCase);
    public bool TargetsInnovator => Target.Equals("Innovator", StringComparison.OrdinalIgnoreCase) 
                                 || Target.Equals("Agent 2", StringComparison.OrdinalIgnoreCase);
    public bool TargetsImplementer => Target.Equals("Implementer", StringComparison.OrdinalIgnoreCase)
                                   || Target.Equals("Agent 3", StringComparison.OrdinalIgnoreCase);
}
