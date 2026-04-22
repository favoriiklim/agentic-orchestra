using AgenticOrchestra.Models;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// Coordinates the 3-Layer Hierarchical Chain pipeline with Squad Pattern:
///   Layer 1: Local Model (Ollama/Gemma) — Communication Interface
///   Layer 2: Web Manager AI — Operational Brain
///   Layer 3: Squad Agents (Innovator + Implementer + Critic) — Parallel Triad
///
/// NORMAL MODE:
///   User → Ollama(classify) → WebManager(execute+squad) → Ollama(present) → User
///
/// HARD FALLBACK MODE (Ollama unavailable):
///   User → WebManager(direct conversation + orchestration) → User
///
/// Supports global CancellationTokenSource for graceful task interruption.
/// </summary>
public sealed class OrchestratorService : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly OllamaAgent _ollamaAgent;
    private readonly PlaywrightWebAgent _webAgent;
    private readonly SessionLoggingService _sessionLogger;
    private readonly AgentManagerService _agentManager;
    private readonly DreamingService _dreamingService;
    
    private readonly List<ChatMessage> _history;
    private bool _webManagerInitialized = false;

    // ── Global Cancellation ──────────────────────────────────────────
    private CancellationTokenSource _globalCts = new();

    /// <summary>
    /// True when Layer 1 (Ollama) is unavailable and the system has bypassed it,
    /// connecting the user directly to the Web Manager AI.
    /// </summary>
    public bool IsHardFallback { get; private set; }

    /// <summary>
    /// True when Layer 1 handled the prompt locally (simple/greeting prompts).
    /// </summary>
    public bool IsLocalOnly { get; private set; }

    public OrchestratorService(AppConfig config)
    {
        _config = config;
        _ollamaAgent = new OllamaAgent(config);
        _webAgent = new PlaywrightWebAgent(config);
        _sessionLogger = new SessionLoggingService();
        _agentManager = new AgentManagerService(_webAgent, _sessionLogger, config);
        _dreamingService = new DreamingService(_webAgent, _sessionLogger, config);
        _history = new List<ChatMessage>
        {
            new ChatMessage { Role = ChatRole.System, Content = config.SystemPrompt }
        };
    }

    /// <summary>
    /// Gets the current active pipeline label for UI display.
    /// </summary>
    public string ActiveProviderName
    {
        get
        {
            if (IsHardFallback) return "⚠️ Hard Fallback · Web Manager AI (Direct)";
            if (IsLocalOnly) return $"Ollama · {_config.Ollama.Model}";
            return $"3-Layer Chain · {_config.Ollama.Model} → Web Manager AI";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CANCELLATION CONTROL
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Cancels the currently running task. Called by --stop or Ctrl+C handler.
    /// Resets the CTS for the next task.
    /// </summary>
    public void CancelCurrentTask()
    {
        if (!_globalCts.IsCancellationRequested)
        {
            _globalCts.Cancel();
            AnsiConsole.MarkupLine("\n[bold red]⛔ STOP signal sent. Cancelling current task...[/]");
        }
    }

    /// <summary>Resets the cancellation token for the next prompt cycle.</summary>
    private void ResetCancellation()
    {
        if (_globalCts.IsCancellationRequested)
        {
            _globalCts.Dispose();
            _globalCts = new CancellationTokenSource();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MAIN PIPELINE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends a prompt through the 3-layer hierarchical chain pipeline.
    /// Checks Layer 1 availability at the start of each cycle for Graceful Degradation.
    /// Supports cancellation via the global CancellationTokenSource.
    /// </summary>
    public async Task<string> ProcessPromptAsync(string prompt)
    {
        ResetCancellation();
        var ct = _globalCts.Token;

        _history.Add(new ChatMessage { Role = ChatRole.User, Content = prompt });
        string responseText;

        try
        {
            // ── Availability Check: Is Layer 1 (Ollama) online? ──────
            bool ollamaAvailable = await _ollamaAgent.IsAvailableAsync();

            if (ollamaAvailable)
            {
                // ═══════════════════════════════════════════════════════
                //  NORMAL MODE: Full 3-Layer Chain
                // ═══════════════════════════════════════════════════════
                IsHardFallback = false;

                // Step 1: Layer 1 classifies the prompt
                ManagerTaskRequest? taskRequest = null;
                await AnsiConsole.Status()
                    .SpinnerStyle(Style.Parse("magenta"))
                    .StartAsync($"(Layer 1 · {_config.Ollama.Model}) Classifying prompt...", async ctx =>
                    {
                        string projectContext = await _sessionLogger.GetMemoryInjectionStringAsync();
                        taskRequest = await _ollamaAgent.ClassifyPromptAsync(prompt, projectContext);
                    });

                if (taskRequest == null)
                {
                    // ── SIMPLE PROMPT: Handle locally ───────────────────
                    IsLocalOnly = true;
                    responseText = await _ollamaAgent.RespondDirectlyAsync(prompt, _history);
                }
                else
                {
                    // ── COMPLEX PROMPT: Delegate to Layer 2 + Squad ─────
                    IsLocalOnly = false;
                    AnsiConsole.MarkupLine($"[dim]Task classified as [cyan]{taskRequest.TaskCategory}[/]. Delegating to Web Manager AI (Layer 2)...[/]");

                    await EnsureWebManagerAsync();
                    var telemetry = await _agentManager.ProcessTaskAsync(taskRequest, ct);

                    // Step 3: Layer 1 presents the telemetry
                    responseText = await _ollamaAgent.PresentTelemetryAsync(telemetry);

                    if (_config.Dreaming.AutoDreamEnabled)
                        await _dreamingService.CheckAndDreamIfNeededAsync(ct);
                }
            }
            else
            {
                // ═══════════════════════════════════════════════════════
                //  HARD FALLBACK MODE: Skip Layer 1, direct to Layer 2
                //  The system MUST NOT crash or lock up when Ollama is down.
                // ═══════════════════════════════════════════════════════
                IsHardFallback = true;
                IsLocalOnly = false;

                if (!_webManagerInitialized)
                {
                    AnsiConsole.MarkupLine("[bold yellow]⚠️  HARD FALLBACK MODE: Local AI (Ollama) is unreachable.[/]");
                    AnsiConsole.MarkupLine("[dim]Bypassing Layer 1. Connecting you directly to the Web Manager AI (Layer 2).[/]");
                    AnsiConsole.MarkupLine("[dim]The system will check Ollama availability on each prompt. Normal mode resumes when Ollama is back online.[/]");
                    AnsiConsole.WriteLine();
                }

                await EnsureWebManagerAsync();

                // Direct pipeline: raw user text → Manager → raw response
                // Manager can still spawn Squad if needed
                responseText = await _agentManager.ProcessDirectAsync(prompt, ct);

                if (_config.Dreaming.AutoDreamEnabled)
                    await _dreamingService.CheckAndDreamIfNeededAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            responseText = "(Task was cancelled by user. Ready for next command.)";
            AnsiConsole.MarkupLine("[yellow]Task cancelled. Worker tabs cleaned up.[/]");
            await _webAgent.CloseAllWorkersAsync();
        }

        _history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = responseText });
        return responseText;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  LOGIN FLOW (--login command)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs the interactive login flow:
    /// 1. Reinitializes browser in visible (non-headless) mode
    /// 2. Opens tabs for each enabled AI platform
    /// 3. Waits for user to log in and press Enter
    /// 4. Closes login tabs and reinitializes in headless mode
    /// </summary>
    public async Task RunLoginFlowAsync()
    {
        AnsiConsole.MarkupLine("\n[bold cyan]🔑 LOGIN FLOW — Opening browser for platform authentication...[/]");

        // Reinitialize browser in visible mode
        bool wasHeadless = _config.WebFallback.Headless;
        await _webAgent.ReinitializeAsync(headless: false);
        _webManagerInitialized = false; // Force re-init after browser rebuild

        // Open login tabs for each enabled platform
        var platforms = await _webAgent.OpenLoginTabsAsync();
        
        if (platforms.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No platforms enabled. Enable platforms in config.json first.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Opened login tabs for: {string.Join(", ", platforms)}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold green]Please log into the web AI platforms in the opened browser window.[/]");
        AnsiConsole.MarkupLine("[bold green]Press ENTER when finished.[/]");
        Console.ReadLine();

        // Close login tabs and persist state
        await _webAgent.CloseLoginTabsAsync();

        // Reinitialize in headless mode (or original mode)
        await _webAgent.ReinitializeAsync(headless: wasHeadless);
        _webManagerInitialized = false;

        AnsiConsole.MarkupLine("[green]✓ Login complete. Browser state saved. Returning to session.[/]\n");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DREAMING & UTILITIES
    // ═══════════════════════════════════════════════════════════════════

    private async Task EnsureWebManagerAsync()
    {
        if (!_webManagerInitialized)
        {
            await _agentManager.InitializeAsync();
            _webManagerInitialized = true;
        }
    }

    public async Task<DreamRecord> TriggerDreamAsync(CancellationToken ct = default)
    {
        await EnsureWebManagerAsync();
        return await _dreamingService.RunDreamCycleAsync(ct);
    }

    public async Task RunExitDreamIfEnabledAsync()
    {
        if (_config.Dreaming.AutoDreamOnExit && await _sessionLogger.GetTelemetryCountAsync() > 0)
        {
            AnsiConsole.MarkupLine("\n[mediumpurple3]💤 Running exit dream analysis...[/]");
            await EnsureWebManagerAsync();
            await _dreamingService.RunDreamCycleAsync();
        }
    }

    public void ClearHistory()
    {
        _history.Clear();
        _history.Add(new ChatMessage { Role = ChatRole.System, Content = _config.SystemPrompt });
    }

    public async ValueTask DisposeAsync()
    {
        _globalCts.Dispose();
        await _webAgent.DisposeAsync();
        await _agentManager.DisposeAsync();
    }
}
