using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.IO;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// Orchestrates interactions between the Head Manager and Worker sub-agents.
/// Implements the 'Zero-Nag' Engineering Initiative with autonomous self-correction.
/// </summary>
public sealed class AgentManagerService : IAsyncDisposable
{
    private readonly PlaywrightWebAgent _webAgent;
    private readonly SessionLoggingService _sessionLogger;
    private readonly ToolExecutionService _toolExecution;
    
    private bool _managerIdentityInitialized = false;
    private int _consecutiveFailures = 0;
    private const int MaxRetryBudget = 5;

    private readonly string[] _delegationKeywords = { "agent", "worker", "expert", "team", "swarm", "ekip", "ajan" };

    public AgentManagerService(PlaywrightWebAgent webAgent, SessionLoggingService sessionLogger, ToolExecutionService toolExecution)
    {
        _webAgent = webAgent;
        _sessionLogger = sessionLogger;
        _toolExecution = toolExecution;
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

    private async Task InitializeManagerIdentityAsync()
    {
        AnsiConsole.MarkupLine("[dim]Injecting System Memory & 'Zero-Nag' Engineering Identity...[/]");
        
        string memoryContext = _sessionLogger.GetMemoryInjectionString();

        var systemPrompt = $@"You are the Head Manager of an Autonomous Multi-Agent Swarm (AgenticOrchestra). You are NOT a standard AI chatbot. You are physically bridged to a Windows OS via a custom C# Execution Middleware. This middleware translates your specific text tokens into real, physical actions on the host machine. You have full read/write access and orchestration powers.

YOUR ARSENAL (You MUST use these exact formats to interact with the world):
1. [TERMINAL_EXEC: command] -> Executes PowerShell/CMD commands on the host. Use this to create folders, run scripts, or check system status.
2. [FILE_READ: filepath] -> Reads the content of a physical file into your context.
3. [FILE_WRITE: filepath | content] -> Writes or overwrites physical files on the disk.
4. [SPAWN: AgentName | Persona & Task] -> Opens a new physical browser tab, creating a sub-agent to research or code parallel tasks.

THE ABSOLUTE LAWS (CRITICAL BEHAVIORAL CONSTRAINTS):
1. NO SIMULATIONS: You are physically connected to a C# middleware. DO NOT simulate, guess, or fake the terminal output. 
2. NO MARKDOWN: DO NOT write markdown code blocks (e.g., ```bash or ```powershell). You MUST strictly use the exact bracket format.
3. WAIT FOR MIDDLEWARE: Just output the command tag and stop generating text. The middleware will execute it and reply in the next turn with the real 'System Outcomes'.
4. NEVER apologize or claim you cannot access the system.
5. Assume the host is a Windows machine using PowerShell unless told otherwise.

{memoryContext}";

        await AnsiConsole.Status()
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync("Overhauling Manager Identity...", async ctx =>
            {
                await _webAgent.SendMessageAsync("Manager", systemPrompt);
            });
    }

    public async Task<string> ProcessUserInputAsync(string userPrompt)
    {
        string currentPrompt = userPrompt;
        string finalResponse = "";

        bool requiresDelegation = _delegationKeywords.Any(k => Regex.IsMatch(userPrompt, $@"\b{k}\b", RegexOptions.IgnoreCase));
        _consecutiveFailures = 0; // Reset budget for new task

        // ── Autonomous "Zero-Prompt" Engineer Loop ──
        while (true)
        {
            string aiResponse = "";
            await AnsiConsole.Status()
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("(Head Manager) is thinking...", async ctx =>
                {
                    aiResponse = await _webAgent.SendMessageAsync("Manager", currentPrompt);
                });

            await _sessionLogger.AddOperationAsync("Manager", currentPrompt, aiResponse);
            finalResponse = aiResponse;

            // ── Mandatory Allocation Check ──
            var spawnMatches = Regex.Matches(aiResponse, @"\[SPAWN:\s*([^\|]+)\|\s*(.+?)\]", RegexOptions.Singleline);
            
            if (requiresDelegation && spawnMatches.Count == 0 && !currentPrompt.Contains("Knowledge Drop (Worker Output)"))
            {
                AnsiConsole.MarkupLine("[bold red]✕ Protocol Intercept:[/] Manager failed to spawn requested physical tabs.");
                currentPrompt = "Protocol Error: You failed to spawn the requested worker tabs. Spawning is mandatory for team requests. Execute [SPAWN] commands now.";
                continue; 
            }

            bool actionExecuted = false;
            var loopFeedBuilder = new System.Text.StringBuilder();

            // 1. Process Physical Spawning Divergence
            if (spawnMatches.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[bold cyan]⚡ Supervisor Divergence Sequence Initiated. Allocating {spawnMatches.Count} node(s)...[/]");
                loopFeedBuilder.AppendLine("System Knowledge Drop (Worker Output):");

                foreach (Match m in spawnMatches)
                {
                    var workerName = m.Groups[1].Value.Trim();
                    var taskInstruction = m.Groups[2].Value.Trim();
                    if (taskInstruction.EndsWith("]")) taskInstruction = taskInstruction.Substring(0, taskInstruction.Length - 1).Trim();

                    string workerResponse = "";
                    await AnsiConsole.Status()
                        .SpinnerStyle(Style.Parse("yellow"))
                        .StartAsync($"({workerName}) Investigating domain...", async ctx =>
                        {
                            workerResponse = await _webAgent.SendMessageAsync(workerName, taskInstruction);
                        });

                    await _sessionLogger.AddOperationAsync(workerName, taskInstruction, workerResponse);
                    AnsiConsole.MarkupLine($"[green]✓ {Markup.Escape(workerName)} context retrieved.[/]");
                    loopFeedBuilder.AppendLine($"\n--- OUTPUT FROM {workerName.ToUpper()} ---\n{workerResponse}");
                    actionExecuted = true;
                }
            }

            // 2. Process Shared Physical Tools (File Read, Terminal, File Write)
            var toolResult = await _toolExecution.ExecuteToolsAsync(aiResponse);
            if (toolResult.ActionsExecuted)
            {
                actionExecuted = true;
                loopFeedBuilder.Append(toolResult.Output);
                
                if (toolResult.BudgetExceeded)
                {
                    return "ABORTED: The Head Manager reached the 5-retry limit trying to fix a persistent error.\n\n" + loopFeedBuilder.ToString();
                }
            }

            if (actionExecuted)
            {
                if (_consecutiveFailures >= MaxRetryBudget)
                {
                    // If we hit the budget, we stop looping and return the failure info to the human
                    return "ABORTED: The Head Manager reached the 5-retry limit trying to fix a persistent error.\n\n" + loopFeedBuilder.ToString();
                }

                currentPrompt = "System Outcomes:\n" + loopFeedBuilder.ToString() + "\nEvaluate results. If an error persists, fix it via [FILE_WRITE]. If done, give final response.";
                continue; 
            }

            break;
        }

        return finalResponse;
    }

    public async ValueTask DisposeAsync()
    {
        await ValueTask.CompletedTask;
    }
}
