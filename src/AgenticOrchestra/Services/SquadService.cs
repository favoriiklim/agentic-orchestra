using System.Text.Json;
using AgenticOrchestra.Models;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// The Squad Execution Engine — implements the fixed-triad asynchronous swarm pattern.
///
/// Structure:
///   Agent 2 (Innovator): Generates ideas, architectural approaches, creative solutions.
///   Agent 3 (Implementer): Writes actual code, shell commands, execution steps.
///   Agent 1 (Critic / Lead Reviewer): Quality gate that approves or rejects work.
///
/// Flow:
///   1. Innovator + Implementer run simultaneously via Task.WhenAll
///   2. Both outputs are fed to the Critic
///   3. If REJECTED → CorrectionPrompt routed to target agent → re-run critic loop
///   4. If APPROVED → return combined output to the Manager
///
/// Each squad member can be assigned to a different AI platform (Gemini/ChatGPT/Claude).
/// All squad tabs are ephemeral and destroyed after the task completes.
/// </summary>
public sealed class SquadService
{
    private readonly PlaywrightWebAgent _webAgent;
    private readonly SessionLoggingService _sessionLogger;
    private readonly AppConfig _config;

    // Fixed squad role names
    private const string InnovatorName = "Squad_Innovator";
    private const string ImplementerName = "Squad_Implementer";
    private const string CriticName = "Squad_Critic";

    public SquadService(PlaywrightWebAgent webAgent, SessionLoggingService sessionLogger, AppConfig config)
    {
        _webAgent = webAgent;
        _sessionLogger = sessionLogger;
        _config = config;
    }

    /// <summary>
    /// Executes the full squad triad pattern for a given task.
    /// Returns the approved combined output from all three agents.
    /// Populates telemetry with worker reports as it executes.
    /// </summary>
    public async Task<string> RunSquadAsync(string task, ManagerTelemetry telemetry, CancellationToken ct = default)
    {
        AnsiConsole.MarkupLine("\n[bold cyan]⚡ SQUAD PATTERN ACTIVATED — Deploying Triad...[/]");

        // ── Assign platforms to squad members ────────────────────────
        _webAgent.AssignPlatform(InnovatorName, _config.Squad.InnovatorPlatform);
        _webAgent.AssignPlatform(ImplementerName, _config.Squad.ImplementerPlatform);
        _webAgent.AssignPlatform(CriticName, _config.Squad.CriticPlatform);

        telemetry.WorkersSpawned.AddRange(new[] { "Innovator", "Implementer", "Critic" });

        // ── PHASE 1: Parallel Execution (Innovator + Implementer) ───
        string innovatorOutput = "";
        string implementerOutput = "";

        var innovatorPrompt = $@"You are Agent 2 — The Innovator. Your role is to generate creative ideas, architectural approaches, and design solutions.

TASK: {task}

Provide innovative approaches, architectural decisions, and design patterns that solve this problem. Focus on the 'WHY' and 'WHAT' — not the implementation details. Be thorough but concise.";

        var implementerPrompt = $@"You are Agent 3 — The Implementer. Your role is to write actual code, shell commands, and step-by-step execution plans.

TASK: {task}

Provide the concrete implementation: working code, exact commands, file structures, and configuration. Focus on the 'HOW' — practical, runnable output. Be precise and complete.";

        AnsiConsole.MarkupLine("[dim]  ├── [cyan]Agent 2 (Innovator)[/] + [cyan]Agent 3 (Implementer)[/] running in parallel...[/]");

        var innovatorReport = new WorkerReport { WorkerName = "Innovator", Task = "Ideas & Architecture" };
        var implementerReport = new WorkerReport { WorkerName = "Implementer", Task = "Code & Execution" };

        try
        {
            // ★ Task.WhenAll — simultaneous execution ★
            var innovatorTask = RunAgentAsync(InnovatorName, innovatorPrompt, ct);
            var implementerTask = RunAgentAsync(ImplementerName, implementerPrompt, ct);

            var results = await Task.WhenAll(innovatorTask, implementerTask);
            innovatorOutput = results[0];
            implementerOutput = results[1];

            innovatorReport.Result = innovatorOutput;
            innovatorReport.Status = "success";
            implementerReport.Result = implementerOutput;
            implementerReport.Status = "success";

            AnsiConsole.MarkupLine("[green]  ├── ✓ Both agents completed.[/]");
        }
        catch (OperationCanceledException)
        {
            innovatorReport.Status = "cancelled";
            implementerReport.Status = "cancelled";
            telemetry.WorkerReports.Add(innovatorReport);
            telemetry.WorkerReports.Add(implementerReport);
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]  ├── ✕ Parallel execution error:[/] {Markup.Escape(ex.Message)}");
            innovatorReport.Status = string.IsNullOrEmpty(innovatorOutput) ? "error" : "success";
            implementerReport.Status = string.IsNullOrEmpty(implementerOutput) ? "error" : "success";
        }

        telemetry.WorkerReports.Add(innovatorReport);
        telemetry.WorkerReports.Add(implementerReport);

        // ── PHASE 2: Critic Review Loop ─────────────────────────────
        AnsiConsole.MarkupLine("[dim]  ├── [yellow]Agent 1 (Critic)[/] reviewing outputs...[/]");

        var criticReport = new WorkerReport { WorkerName = "Critic", Task = "Quality Review" };
        int criticRetries = 0;

        while (criticRetries < _config.Squad.MaxCriticRetries)
        {
            ct.ThrowIfCancellationRequested();
            criticRetries++;

            var criticPrompt = $@"You are Agent 1 — The Critic / Lead Reviewer. You are the quality gate for a 3-agent squad.

You have received outputs from two agents working on this task: ""{task}""

=== INNOVATOR OUTPUT (Agent 2) ===
{innovatorOutput}

=== IMPLEMENTER OUTPUT (Agent 3) ===
{implementerOutput}

YOUR JOB: Review both outputs for quality, correctness, completeness, and coherence.

RESPOND WITH EXACTLY THIS JSON (no markdown fences, no extra text):
{{""Status"": ""APPROVED"", ""Target"": """", ""CorrectionPrompt"": """"}}

OR if work needs correction:
{{""Status"": ""REJECTED"", ""Target"": ""Innovator"" or ""Implementer"", ""CorrectionPrompt"": ""Your specific correction instructions here""}}

Rules:
- Only REJECT if there are genuine issues (bugs, missing logic, incoherent design).
- If both are good enough, APPROVE.
- After {_config.Squad.MaxCriticRetries} total reviews, you MUST approve.
- This is review #{criticRetries} of {_config.Squad.MaxCriticRetries}.";

            var criticResponse = await RunAgentAsync(CriticName, criticPrompt, ct);
            
            // ── Parse Critic's JSON Response ────────────────────────
            var review = ParseCriticResponse(criticResponse);

            if (review == null || review.IsApproved)
            {
                AnsiConsole.MarkupLine($"[green]  ├── ✓ Agent 1 (Critic) APPROVED (round {criticRetries}).[/]");
                criticReport.Result = criticResponse;
                criticReport.Status = "success";
                break;
            }

            // ── REJECTED: Route correction back to target ───────────
            AnsiConsole.MarkupLine($"[yellow]  ├── ↻ Agent 1 REJECTED (round {criticRetries}/{_config.Squad.MaxCriticRetries}). Target: {Markup.Escape(review.Target)}[/]");

            if (review.TargetsInnovator)
            {
                AnsiConsole.MarkupLine("[dim]  │   └── Re-running Innovator with corrections...[/]");
                var correctionPrompt = $@"CORRECTION from the Lead Reviewer:
{review.CorrectionPrompt}

Your previous output was:
{innovatorOutput}

The original task was: {task}

Please revise your output based on the correction above.";

                innovatorOutput = await RunAgentAsync(InnovatorName, correctionPrompt, ct);
                innovatorReport.Result = innovatorOutput;
            }
            else if (review.TargetsImplementer)
            {
                AnsiConsole.MarkupLine("[dim]  │   └── Re-running Implementer with corrections...[/]");
                var correctionPrompt = $@"CORRECTION from the Lead Reviewer:
{review.CorrectionPrompt}

Your previous output was:
{implementerOutput}

The original task was: {task}

Please revise your output based on the correction above.";

                implementerOutput = await RunAgentAsync(ImplementerName, correctionPrompt, ct);
                implementerReport.Result = implementerOutput;
            }

            // Close the critic tab to get a fresh context for re-review
            await _webAgent.CloseWorkerTabAsync(CriticName);

            if (criticRetries >= _config.Squad.MaxCriticRetries)
            {
                AnsiConsole.MarkupLine($"[yellow]  ├── ⚠ Critic retry limit ({_config.Squad.MaxCriticRetries}) reached. Force-approving.[/]");
                criticReport.Result = "Force-approved after max retries.";
                criticReport.Status = "force_approved";
            }
        }

        telemetry.WorkerReports.Add(criticReport);

        // ── PHASE 3: Cleanup squad tabs ─────────────────────────────
        await _webAgent.CloseWorkerTabAsync(InnovatorName);
        await _webAgent.CloseWorkerTabAsync(ImplementerName);
        await _webAgent.CloseWorkerTabAsync(CriticName);

        AnsiConsole.MarkupLine("[bold cyan]⚡ SQUAD PATTERN COMPLETE — Results synthesized.[/]\n");

        // ── Compose final combined output for the Manager ───────────
        return $@"=== SQUAD EXECUTION RESULTS ===

--- INNOVATOR (Agent 2) ---
{innovatorOutput}

--- IMPLEMENTER (Agent 3) ---
{implementerOutput}

--- CRITIC VERDICT ---
APPROVED after {criticRetries} review round(s).";
    }

    /// <summary>
    /// Runs a single squad agent and logs the operation.
    /// </summary>
    private async Task<string> RunAgentAsync(string agentName, string prompt, CancellationToken ct)
    {
        string response = await _webAgent.SendMessageAsync(agentName, prompt, ct);

        await _sessionLogger.AddOperationAsync(agentName, prompt, response);
        return response;
    }

    /// <summary>
    /// Parses the Critic's strict JSON response. Falls back to APPROVED if parsing fails.
    /// </summary>
    private static SquadReviewResult? ParseCriticResponse(string response)
    {
        var jsonContent = response.Trim();

        // Strip markdown fences if present
        if (jsonContent.StartsWith("```"))
        {
            var firstNewline = jsonContent.IndexOf('\n');
            if (firstNewline > 0) jsonContent = jsonContent[(firstNewline + 1)..];
            if (jsonContent.EndsWith("```"))
                jsonContent = jsonContent[..^3];
            jsonContent = jsonContent.Trim();
        }

        // Try to find JSON object in the response
        var jsonStart = jsonContent.IndexOf('{');
        var jsonEnd = jsonContent.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            jsonContent = jsonContent[jsonStart..(jsonEnd + 1)];
        }

        try
        {
            return JsonSerializer.Deserialize<SquadReviewResult>(jsonContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            // If we can't parse JSON at all, default to REJECTED so the Critic has to retry
            return new SquadReviewResult 
            { 
                Status = "REJECTED", 
                Target = "Innovator", 
                CorrectionPrompt = "Your previous output was malformed JSON. You MUST respond with exactly the requested JSON format and absolutely no other text."
            };
        }
    }
}
