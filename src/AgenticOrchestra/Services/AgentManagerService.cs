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
    
    // Thread-safe queue for incoming worker messages/results waiting to be reviewed by Manager
    private readonly ConcurrentQueue<string> _workerQueue = new();

    public AgentManagerService(PlaywrightWebAgent webAgent)
    {
        _webAgent = webAgent;
    }

    public async Task InitializeAsync()
    {
        // Boot the stateful Playwright cluster
        await _webAgent.InitializeAsync();
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

        // ── TRIGGER-BASED WORKFLOW ──
        if (response.Contains("[ACTION:EXECUTE_PROJECT]", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("\n[bold cyan]⚡ Manager initiated EXECUTE_PROJECT sequence. Booting Workers...[/]");
            return await ExecuteMultiAgentWorkflowAsync(response);
        }

        return response;
    }

    /// <summary>
    /// Decomposes the task, spawns workers, handles queueing, and invokes the QA loop.
    /// </summary>
    private async Task<string> ExecuteMultiAgentWorkflowAsync(string managerPlan)
    {
        // In a true parsed flow, we would deserialize `managerPlan` from JSON to build tasks.
        // For architectural setup, we simulate enqueuing a parsed task for 'Worker 1'.
        
        _workerQueue.Enqueue($"Execute the following sub-task derived from the master plan:\n{managerPlan}");

        string finalProjectOutput = "";

        // ── SEQUENTIAL MESSAGE QUEUE ──
        while (_workerQueue.TryDequeue(out var taskInstruction))
        {
            bool taskCompleted = false;
            int retries = 0;
            string workerResponse = "";
            string workerName = "Worker 1"; // This would dynamically come from the JSON map

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
