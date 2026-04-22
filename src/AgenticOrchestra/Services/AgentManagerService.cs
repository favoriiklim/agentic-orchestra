using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using AgenticOrchestra.Models;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// Layer 2 Orchestrator: Centers the autonomous loop around the Web Manager AI.
/// The Manager tab is PERSISTENT (never closes), and all Worker/Squad sub-tabs (Layer 3)
/// open and close based on the Manager's commands.
///
/// Communication modes:
///   NORMAL MODE:  Receives ManagerTaskRequest JSON → returns ManagerTelemetry JSON
///   FALLBACK MODE: Receives raw user text → returns raw response text (no JSON wrapping)
///
/// Squad integration: When the Manager requests parallel expertise, the SquadService
/// handles the fixed-triad (Innovator + Implementer → Critic) pattern.
///
/// Supports CancellationToken for graceful task interruption via --stop / Ctrl+C.
/// </summary>
public sealed class AgentManagerService : IAsyncDisposable
{
    private readonly PlaywrightWebAgent _webAgent;
    private readonly SessionLoggingService _sessionLogger;
    private readonly NativeFileService _fileService;
    private readonly NativeTerminalService _terminalService;
    private readonly SquadService _squadService;
    private readonly AppConfig _config;
    
    private bool _managerIdentityInitialized = false;
    private int _consecutiveFailures = 0;
    private const int MaxRetryBudget = 5;

    public AgentManagerService(PlaywrightWebAgent webAgent, SessionLoggingService sessionLogger, AppConfig config)
    {
        _webAgent = webAgent;
        _sessionLogger = sessionLogger;
        _config = config;
        _fileService = new NativeFileService();
        _terminalService = new NativeTerminalService(config.Timeouts.TerminalCommandSeconds);
        _squadService = new SquadService(webAgent, sessionLogger, config);
    }

    public async Task InitializeAsync()
    {
        await _webAgent.InitializeAsync();
        await _sessionLogger.InitializeAsync();

        if (!_managerIdentityInitialized)
        {
            await InitializeManagerIdentityAsync();
            _managerIdentityInitialized = true;
        }
    }

    /// <summary>
    /// Injects the 3-Layer Hierarchy system identity into the persistent Manager tab.
    /// General-purpose architect — no domain-specific bias.
    /// </summary>
    private async Task InitializeManagerIdentityAsync()
    {
        AnsiConsole.MarkupLine("[dim]Injecting 3-Layer Hierarchy Identity & Memory into Web Manager AI...[/]");
        
        string memoryContext = _sessionLogger.GetMemoryInjectionString();

        var systemPrompt = $@"You are the Web Manager AI — a General-Purpose Autonomous Senior Software Architect & System Operator.
You are the central operational brain of a 3-Layer Autonomous Engineering System (AgenticOrchestra).
You adapt to whatever domain or technology the user requests. You never assume a specific project context.

=== YOUR POSITION IN THE HIERARCHY ===
Layer 1: Local Model (Gemma/Ollama) — Communication interface only. It classifies tasks and presents your results.
Layer 2: YOU (Web Manager AI) — The main analytical brain, memory holder, and orchestration heart.
Layer 3: Squad Agents — A fixed triad of sub-agents you can deploy for parallel expertise:
  - Agent 2 (Innovator): Ideas, architecture, design
  - Agent 3 (Implementer): Code, commands, execution
  - Agent 1 (Critic): Quality gate, reviews & approves

=== YOUR PHYSICAL ARSENAL (Exact token formats for real actions) ===
1. [TERMINAL_EXEC: command] → Executes PowerShell/CMD commands on the host Windows OS.
2. [FILE_READ: filepath] → Reads a physical file's content into your context.
3. [FILE_WRITE: filepath | content] → Writes or overwrites physical files on disk.
4. [SPAWN_SQUAD: task description] → Deploys the full Squad triad for complex tasks requiring parallel expertise.

=== CRITICAL RULES ===
1. Squad Agents (Layer 3) NEVER talk to Layer 1. They report ONLY to you through the middleware.
2. Use [SPAWN_SQUAD] when tasks benefit from creative + implementation perspectives simultaneously.
3. When you finish a task, provide a clear final summary of what was accomplished.
4. NEVER apologize or claim you cannot access the system. You interact with it via the tokens above.
5. NEVER ask for permission. If given a task, immediately start the Scan-Plan-Execute cycle.
6. If a command fails, analyze the error and retry autonomously (up to 5 times).
7. Assume the host is Windows with PowerShell unless told otherwise.
8. You are domain-agnostic. Adapt your expertise to whatever technology stack the user needs.

{memoryContext}

Acknowledge your role as the Web Manager AI (Layer 2) and await task instructions.";

        await AnsiConsole.Status()
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync("Initializing Web Manager AI (Layer 2)...", async ctx =>
            {
                await _webAgent.SendMessageAsync("Manager", systemPrompt);
            });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  NORMAL MODE: ManagerTaskRequest JSON → ManagerTelemetry JSON
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Processes a classified task from Layer 1 (Local Model) via Strict JSON protocol.
    /// Returns a ManagerTelemetry with the full execution summary.
    /// </summary>
    public async Task<ManagerTelemetry> ProcessTaskAsync(ManagerTaskRequest taskRequest, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var telemetry = new ManagerTelemetry
        {
            TaskId = taskRequest.TaskId,
            TaskExecuted = taskRequest.UserPrompt
        };

        var managerPrompt = $@"### NEW TASK FROM LOCAL MODEL (Layer 1) ###
Task ID: {taskRequest.TaskId}
Category: {taskRequest.TaskCategory}
User Request: {taskRequest.UserPrompt}
Project Context: {taskRequest.ProjectContext}

Execute this task using your arsenal. Use [SPAWN_SQUAD] for tasks needing parallel expertise. When COMPLETELY done, provide your final output.";

        string finalResponse;
        try
        {
            finalResponse = await RunAutonomousLoop(managerPrompt, telemetry, ct);
        }
        catch (OperationCanceledException)
        {
            telemetry.ErrorsHandled = "Task cancelled by user.";
            telemetry.FinalOutcome = "CANCELLED: Task was interrupted by the user.";
            stopwatch.Stop();
            telemetry.ExecutionTimeSeconds = stopwatch.Elapsed.TotalSeconds;
            await _webAgent.CloseAllWorkersAsync();
            return telemetry;
        }
        catch (Exception ex)
        {
            telemetry.ErrorsHandled = $"Critical loop failure: {ex.Message}";
            telemetry.FinalOutcome = $"Task failed with exception: {ex.Message}";
            stopwatch.Stop();
            telemetry.ExecutionTimeSeconds = stopwatch.Elapsed.TotalSeconds;
            return telemetry;
        }

        stopwatch.Stop();
        telemetry.ExecutionTimeSeconds = stopwatch.Elapsed.TotalSeconds;
        telemetry.FinalOutcome = finalResponse;

        await _webAgent.CloseAllWorkersAsync();
        await _sessionLogger.AddTelemetryAsync(telemetry);

        return telemetry;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  HARD FALLBACK MODE: Raw text → Raw response (no JSON wrapping)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Direct pipeline for Hard Fallback Mode when Layer 1 (Ollama) is unavailable.
    /// The user's raw text goes straight to the Manager, and the response comes back as-is.
    /// No JSON telemetry wrapping — the Manager acts as both conversational interface AND orchestrator.
    /// </summary>
    public async Task<string> ProcessDirectAsync(string userPrompt, CancellationToken ct = default)
    {
        var telemetry = new ManagerTelemetry
        {
            TaskExecuted = userPrompt
        };
        var stopwatch = Stopwatch.StartNew();

        string finalResponse;
        try
        {
            finalResponse = await RunAutonomousLoop(userPrompt, telemetry, ct);
        }
        catch (OperationCanceledException)
        {
            finalResponse = "(Task cancelled by user.)";
        }
        catch (Exception ex)
        {
            finalResponse = $"[Manager Error] {ex.Message}";
        }

        stopwatch.Stop();
        telemetry.ExecutionTimeSeconds = stopwatch.Elapsed.TotalSeconds;
        telemetry.FinalOutcome = finalResponse;

        await _webAgent.CloseAllWorkersAsync();
        await _sessionLogger.AddTelemetryAsync(telemetry);

        return finalResponse;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CORE AUTONOMOUS LOOP (Shared by both modes)
    // ═══════════════════════════════════════════════════════════════════

    private async Task<string> RunAutonomousLoop(string initialPrompt, ManagerTelemetry telemetry, CancellationToken ct)
    {
        string currentPrompt = initialPrompt;
        string finalResponse = "";
        _consecutiveFailures = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string aiResponse = "";
            await AnsiConsole.Status()
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("(Web Manager AI) is thinking...", async ctx =>
                {
                    aiResponse = await _webAgent.SendMessageAsync("Manager", currentPrompt, ct);
                });

            await _sessionLogger.AddOperationAsync("Manager", currentPrompt, aiResponse);
            finalResponse = aiResponse;

            bool actionExecuted = false;
            var loopFeedBuilder = new System.Text.StringBuilder();

            // ── 1. Process [SPAWN_SQUAD] — Squad Triad Deployment ───
            var squadMatches = Regex.Matches(aiResponse, @"\[SPAWN_SQUAD:\s*(.+?)\]", RegexOptions.Singleline);
            if (squadMatches.Count > 0)
            {
                foreach (Match m in squadMatches)
                {
                    var squadTask = m.Groups[1].Value.Trim();
                    if (squadTask.EndsWith("]")) squadTask = squadTask[..^1].Trim();

                    var squadResult = await _squadService.RunSquadAsync(squadTask, telemetry, ct);
                    loopFeedBuilder.AppendLine("System Knowledge Drop (Squad Output):");
                    loopFeedBuilder.AppendLine(squadResult);
                    actionExecuted = true;
                }
            }

            // ── 2. Process legacy [SPAWN] — Single worker (backward compat) ─
            var spawnMatches = Regex.Matches(aiResponse, @"\[SPAWN:\s*([^\|]+)\|\s*(.+?)\]", RegexOptions.Singleline);
            if (spawnMatches.Count > 0 && squadMatches.Count == 0) // Only if no squad was triggered
            {
                AnsiConsole.MarkupLine($"\n[bold cyan]⚡ Manager spawning {spawnMatches.Count} Worker Agent(s) (Layer 3)...[/]");
                loopFeedBuilder.AppendLine("System Knowledge Drop (Worker Output):");

                foreach (Match m in spawnMatches)
                {
                    ct.ThrowIfCancellationRequested();
                    var workerName = m.Groups[1].Value.Trim();
                    var taskInstruction = m.Groups[2].Value.Trim();
                    if (taskInstruction.EndsWith("]")) taskInstruction = taskInstruction[..^1].Trim();

                    telemetry.WorkersSpawned.Add(workerName);
                    var workerReport = new WorkerReport { WorkerName = workerName, Task = taskInstruction };

                    string workerResponse = "";
                    try
                    {
                        await AnsiConsole.Status()
                            .SpinnerStyle(Style.Parse("yellow"))
                            .StartAsync($"({workerName}) Working on task...", async ctx =>
                            {
                                workerResponse = await _webAgent.SendMessageAsync(workerName, taskInstruction, ct);
                            });

                        await _sessionLogger.AddOperationAsync(workerName, taskInstruction, workerResponse);
                        AnsiConsole.MarkupLine($"[green]✓ {Markup.Escape(workerName)} completed. Reporting to Manager.[/]");
                        workerReport.Result = workerResponse;
                        workerReport.Status = "success";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        workerResponse = $"(Worker Error: {ex.Message})";
                        workerReport.Result = workerResponse;
                        workerReport.Status = "error";
                        AnsiConsole.MarkupLine($"[red]✕ {Markup.Escape(workerName)} failed:[/] {Markup.Escape(ex.Message)}");
                    }

                    telemetry.WorkerReports.Add(workerReport);
                    loopFeedBuilder.AppendLine($"\n--- OUTPUT FROM {workerName.ToUpper()} ---\n{workerResponse}");
                    await _webAgent.CloseWorkerTabAsync(workerName);
                    actionExecuted = true;
                }
            }

            // ── 3. Process [FILE_READ] ──────────────────────────────
            var readMatches = Regex.Matches(aiResponse, @"\[FILE_READ:\s*([^\]]+)\]");
            foreach (Match m in readMatches)
            {
                var path = m.Groups[1].Value.Trim();
                AnsiConsole.MarkupLine($"[dim cyan]Native Agent reading:[/] {Markup.Escape(path)}");
                var content = _fileService.ReadFile(path);
                loopFeedBuilder.AppendLine($"Result of FILE_READ '{path}':\n```\n{content}\n```\n");
                actionExecuted = true;
            }

            // ── 4. Process [TERMINAL_EXEC] (with Budget Tracking) ───
            var termMatches = Regex.Matches(aiResponse, @"\[TERMINAL_EXEC:\s*([^\]]+)\]");
            foreach (Match m in termMatches)
            {
                ct.ThrowIfCancellationRequested();
                var cmd = m.Groups[1].Value.Trim();
                var result = await _terminalService.ExecuteCommandAsync(cmd);
                
                bool isError = result.Contains("Error", StringComparison.OrdinalIgnoreCase) || 
                               result.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
                               result.Contains("Exception", StringComparison.OrdinalIgnoreCase);

                if (isError)
                {
                    _consecutiveFailures++;
                    telemetry.RetryCount = _consecutiveFailures;
                    AnsiConsole.MarkupLine($"[bold red]✕ Command failed ({_consecutiveFailures}/{MaxRetryBudget}).[/]");
                }
                else
                {
                    _consecutiveFailures = 0;
                }

                loopFeedBuilder.AppendLine($"Result of TERMINAL_EXEC '{cmd}':\n```\n{result}\n```\n");
                actionExecuted = true;

                if (_consecutiveFailures >= MaxRetryBudget)
                {
                    AnsiConsole.MarkupLine("[bold red]FATAL: Bug-fix budget exhausted. Halting autonomous loop.[/]");
                    telemetry.ErrorsHandled = $"Bug-fix budget exhausted after {MaxRetryBudget} consecutive failures.";
                    loopFeedBuilder.AppendLine("\nERROR: Bug-fix budget exceeded (5 retries). Please intervene manually.");
                    break;
                }
            }

            // ── 5. Process [FILE_WRITE] (Executioner Pattern) ───────
            var writeMatches = Regex.Matches(aiResponse, @"\[FILE_WRITE:\s*(?<path>.*?)\s*\|\s*(?<content>.*?)\]", RegexOptions.Singleline);
            foreach (Match m in writeMatches)
            {
                var path = m.Groups["path"].Value.Trim();
                var content = m.Groups["content"].Value.Trim();
                
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, content);
                    
                    AnsiConsole.MarkupLine($"[bold green][[SUCCESS]][/] File Written: {Markup.Escape(path)}");
                    loopFeedBuilder.AppendLine($"Result of FILE_WRITE '{path}': Success.");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[bold red]✕ Write Failed for {Markup.Escape(path)}:[/] {Markup.Escape(ex.Message)}");
                    loopFeedBuilder.AppendLine($"Result of FILE_WRITE '{path}': FAILED - {ex.Message}");
                }
                
                actionExecuted = true;
            }

            // ── Loop Decision ───────────────────────────────────────
            if (actionExecuted)
            {
                if (_consecutiveFailures >= MaxRetryBudget)
                {
                    return "ABORTED: Bug-fix budget exhausted.\n\n" + loopFeedBuilder.ToString();
                }

                currentPrompt = "System Outcomes:\n" + loopFeedBuilder.ToString() + "\nEvaluate results. If an error persists, fix it. If done, give final response.";
                continue; 
            }

            break;
        }

        return finalResponse;
    }

    public async ValueTask DisposeAsync()
    {
        _terminalService.Dispose();
        await ValueTask.CompletedTask;
    }
}
