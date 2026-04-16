using System.Collections.Concurrent;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// Orchestrates interactions between the Head Manager and Worker sub-agents.
/// Handles multi-agent routing, message queues, and quality assurance loops.
/// </summary>
public sealed class AgentManagerService
{
    private readonly PlaywrightWebAgent _webAgent;
    private readonly SessionLoggingService _sessionLogger;
    
    // Thread-safe queue for incoming worker messages/results waiting to be reviewed by Manager
    private readonly ConcurrentQueue<string> _workerQueue = new();
    private bool _managerIdentityInitialized = false;

    public AgentManagerService(PlaywrightWebAgent webAgent, SessionLoggingService sessionLogger)
    {
        _webAgent = webAgent;
        _sessionLogger = sessionLogger;
    }

    public async Task InitializeAsync()
    {
        // Boot the stateful Playwright cluster & Session Map
        await _webAgent.InitializeAsync();
        await _sessionLogger.InitializeAsync();

        if (!_managerIdentityInitialized)
        {
            await InitializeManagerIdentityAsync();
            _managerIdentityInitialized = true;
        }
    }

    /// <summary>
    /// Injects the strict system rules, roles, and the past Session Memory into the Manager Agent.
    /// This happens silently in the background before the user can interact.
    /// </summary>
    private async Task InitializeManagerIdentityAsync()
    {
        AnsiConsole.MarkupLine("[dim]Injecting System Memory & Identity rules into the Manager Agent...[/]");
        
        string memoryContext = _sessionLogger.GetMemoryInjectionString();

        var systemPrompt = $@"You are the Head Manager of a Multi-Agent autonomous swarm.
Your job is to orchestrate complex sub-tasks, parse user requirements, and ensure output quality.
When you receive a project that requires worker execution, you MUST output the exact phrase: [ACTION:EXECUTE_PROJECT]

Available Workers: 'Worker 1', 'Coder', 'Writer'.

{memoryContext}

Acknowledge these instructions and confirm your readiness silently (respond with 'Ready' only).";

        await AnsiConsole.Status()
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync("Bootstrapping Manager Identity...", async ctx =>
            {
                await _webAgent.SendMessageAsync("Manager", systemPrompt);
            });
    }

    /// <summary>
    /// Processes standard user interactions through the Head Manager tab.
    /// Acts as the trigger sensor for automated orchestration.
    /// </summary>
    public async Task<string> ProcessUserInputAsync(string userPrompt)
    {
        string response = "";

        await AnsiConsole.Status()
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync("Head Manager is thinking...", async ctx =>
            {
                response = await _webAgent.SendMessageAsync("Manager", userPrompt);
            });

        // Save the raw Manager conversation mapping
        await _sessionLogger.AddOperationAsync("Manager", userPrompt, response);

        // ── TRIGGER-BASED WORKFLOW ──
        if (response.Contains("[ACTION:EXECUTE_PROJECT]", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("\n[bold cyan]⚡ Manager initiated EXECUTE_PROJECT sequence. Booting Workers...[/]");
            string finalProjectOutput = await ExecuteMultiAgentWorkflowAsync(response);
            
            // Re-summarize project memory context. We extract the first 150 chars as a rough state summary for now.
            string briefSummary = response.Length > 150 ? response[..150] + "..." : response;
            await _sessionLogger.UpdateProjectStateAsync($"An EXECUTE_PROJECT workflow completed. Latest directives: {briefSummary}");

            return finalProjectOutput;
        }

        return response;
    }

    /// <summary>
    /// Decomposes the task, spawns workers, handles queueing, and invokes the QA loop.
    /// </summary>
    private async Task<string> ExecuteMultiAgentWorkflowAsync(string managerPlan)
    {
        // Enqueuing derived task map. In production, this parses JSON into discrete `workerName` and `task` bounds.
        _workerQueue.Enqueue($"Execute the following sub-task derived from the master plan:\n{managerPlan}");

        string finalProjectOutput = "";

        // ── SEQUENTIAL MESSAGE QUEUE ──
        while (_workerQueue.TryDequeue(out var taskInstruction))
        {
            bool taskCompleted = false;
            int retries = 0;
            string workerResponse = "";
            string workerName = "Worker 1"; // This would dynamically come from the parsed JSON instruction map

            // ── FEEDBACK / QUALITY LOOP ──
            while (!taskCompleted && retries < 3)
            {
                // Give task to worker
                await AnsiConsole.Status()
                    .SpinnerStyle(Style.Parse("yellow"))
                    .StartAsync($"({workerName}) Executing task instructions...", async ctx =>
                    {
                        workerResponse = await _webAgent.SendMessageAsync(workerName, taskInstruction);
                    });

                await _sessionLogger.AddOperationAsync(workerName, taskInstruction, workerResponse);

                AnsiConsole.MarkupLine($"[green]✓ {workerName} output produced.[/]");
                AnsiConsole.MarkupLine($"[dim]({workerName} -> Manager) Transferring data for review...[/]");

                string reviewResponse = "";

                // Send Worker's output back to Manager for QA validation
                await AnsiConsole.Status()
                    .SpinnerStyle(Style.Parse("cyan"))
                    .StartAsync("(Manager) Reviewing worker output...", async ctx =>
                    {
                        // Wrap the output in a strict review prompt
                        string qaPrompt = $"Review this output from {workerName}. If it fails requirements or has errors, reply with [REJECT] and specific feedback. If it succeeds, provide the final summarized output.\n\nOutput:\n{workerResponse}";
                        reviewResponse = await _webAgent.SendMessageAsync("Manager", qaPrompt);
                    });

                await _sessionLogger.AddOperationAsync("Manager", $"QA Review for {workerName}", reviewResponse);

                if (reviewResponse.Contains("[REJECT]", StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine($"[bold red]✕ (Manager -> {workerName}) Task REJECTED.[/] Initiating retry loop...");
                    
                    // Feedback loop: Inject manager's critique back to the worker
                    taskInstruction = $"The Manager rejected your previous work with the following feedback:\n{reviewResponse}\n\nPlease retry and correct the issues.";
                    retries++;
                }
                else
                {
                    AnsiConsole.MarkupLine($"[bold green]✓ (Manager -> {workerName}) Task APPROVED.[/]");
                    taskCompleted = true;
                    finalProjectOutput += reviewResponse + "\n";
                }
            }

            if (!taskCompleted)
            {
                AnsiConsole.MarkupLine($"[bold red]Fatal: {workerName} failed to satisfy Manager after 3 retries.[/]");
                return $"(Error) Sub-agent {workerName} failed execution loop.";
            }
        }

        AnsiConsole.MarkupLine("[bold cyan]★ Multi-Agent sequence completed successfully.[/]\n");
        return finalProjectOutput;
    }
}
