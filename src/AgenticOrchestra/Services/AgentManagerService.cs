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
    private readonly ToolExecutionService _toolService;
    private readonly AppConfig _config;
    
    private bool _managerIdentityInitialized = false;

    public AgentManagerService(PlaywrightWebAgent webAgent, SessionLoggingService sessionLogger, AppConfig config)
    {
        _webAgent = webAgent;
        _sessionLogger = sessionLogger;
        _config = config;
        _fileService = new NativeFileService();
        _terminalService = new NativeTerminalService(config.Timeouts.TerminalCommandSeconds);
        _squadService = new SquadService(webAgent, sessionLogger, config);
        _toolService = new ToolExecutionService(config);
        _toolService.SetAgentManager(this);
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

8. You are domain-agnostic. Adapt your expertise to whatever technology stack the user needs.

=== USER CONFIGURATION SYSTEM PROMPT ===
{_config.SystemPrompt}

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
        finally
        {
            await _webAgent.CloseAllWorkersAsync();
        }

        stopwatch.Stop();
        telemetry.ExecutionTimeSeconds = stopwatch.Elapsed.TotalSeconds;
        telemetry.FinalOutcome = finalResponse;

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
        finally
        {
            await _webAgent.CloseAllWorkersAsync();
        }

        stopwatch.Stop();
        telemetry.ExecutionTimeSeconds = stopwatch.Elapsed.TotalSeconds;
        telemetry.FinalOutcome = finalResponse;

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
        
        _toolService.ResetBudget();
        int totalIterations = 0;
        const int MaxTotalIterations = 15;

        while (totalIterations < MaxTotalIterations)
        {
            totalIterations++;
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

            var result = await _toolService.ExecuteToolsAsync(aiResponse, telemetry, ct);

            if (result.BudgetExceeded)
            {
                return "ABORTED: Bug-fix budget exhausted.\n\n" + result.Output;
            }

            if (result.ActionsExecuted)
            {
                currentPrompt = "System Outcomes:\n" + result.Output + "\nEvaluate results. If an error persists, fix it. If done, give final response.";
                continue; 
            }

            break; // No actions executed, exit loop
        }

        if (totalIterations >= MaxTotalIterations)
        {
            AnsiConsole.MarkupLine("[bold yellow]Loop Iteration Limit Reached (15). Halting to prevent infinite loop.[/]");
            finalResponse += "\n[ABORTED: Max loop iterations reached]";
        }

        return finalResponse;
    }

    public async Task<string> RunSquadProxyAsync(string taskString, ManagerTelemetry telemetry, CancellationToken ct)
    {
        return await _squadService.RunSquadAsync(taskString, telemetry, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _terminalService.Dispose();
        await ValueTask.CompletedTask;
    }
}
