using System.Text.RegularExpressions;
using AgenticOrchestra.Models;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// Coordinates the hybrid AI pipeline.
/// Attempts to use the local Ollama instance first.
/// If unavailable, it falls back to the Playwright web automation agent.
/// </summary>
public sealed class OrchestratorService : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly OllamaAgent _ollamaAgent;
    private readonly PlaywrightWebAgent _webAgent;
    private readonly SessionLoggingService _sessionLogger;
    private readonly AgentManagerService _agentManager;
    private readonly ToolExecutionService _toolExecution;
    
    private readonly List<ChatMessage> _history;

    /// <summary>
    /// Indicates whether the current active provider is the local Ollama instance.
    /// Can be used by the UI to display the active mode.
    /// </summary>
    public bool IsUsingLocalAgent { get; private set; }

    public OrchestratorService(AppConfig config)
    {
        _config = config;
        _ollamaAgent = new OllamaAgent(config);
        _webAgent = new PlaywrightWebAgent(config);
        _sessionLogger = new SessionLoggingService();
        _toolExecution = new ToolExecutionService(config);
        _agentManager = new AgentManagerService(_webAgent, _sessionLogger, _toolExecution);
        _history = new List<ChatMessage>();
    }

    /// <summary>
    /// Loads a historic state into the live orchestrator memory.
    /// </summary>
    public void LoadState(SessionData session)
    {
        _history.Clear();
        _history.AddRange(session.RawHistory);
    }

    /// <summary>
    /// Gets the current active provider label.
    /// </summary>
    public string ActiveProviderName => IsUsingLocalAgent 
        ? $"Ollama · {_config.Ollama.Model}" 
        : "Web Session";

    /// <summary>
    /// Sends a prompt through the orchestration pipeline.
    /// Manages the internal conversation history.
    /// </summary>
    public async Task<string> ProcessPromptAsync(string prompt)
    {
        // 1. Dreaming logic injection
        if (prompt.Trim() == "[SYSTEM_DREAMING_TRIGGER]")
        {
            AnsiConsole.MarkupLine("\n[dim purple]💤 Agent is Dreaming (Background Analysis)...[/]");
            prompt = "You are now in dreaming mode. Analyze the current project state, compress your long-term memory summary, and suggest 3 innovative improvements or code optimizations for the current environment. Keep it robust and forward-thinking.";
        }

        // 2. Add user prompt to history
        _history.Add(new ChatMessage { Role = ChatRole.User, Content = prompt });

        await _sessionLogger.UpdateRawHistoryAsync(_history);

        string responseText = string.Empty;

        // 3. Fallback Logic Pipeline
        IsUsingLocalAgent = await _ollamaAgent.IsAvailableAsync();
        bool fallbackToWeb = !IsUsingLocalAgent;

        if (IsUsingLocalAgent)
        {
            _toolExecution.ResetBudget();
            try
            {
                while (true)
                {
                    // Path A: Local execution via Ollama
                    await AnsiConsole.Status()
                        .SpinnerStyle(Style.Parse("magenta"))
                        .StartAsync($"Agent is thinking ([dim]{ActiveProviderName}[/])...", async ctx =>
                        {
                            // --- DYNAMIC CONTEXT INJECTION (Just-In-Time) ---
                            string memoryContext = _sessionLogger.GetMemoryInjectionString();
                            string systemRules = _config.SystemPrompt;

                            // ── Layer 1: System Role (weak but still useful as baseline) ──
                            var payload = new List<ChatMessage>
                            {
                                new ChatMessage { Role = ChatRole.System, Content = systemRules }
                            };

                            // ── Layer 2: Few-Shot Demonstration (teaches FORMAT by example) ──
                            // This is the most powerful technique to force strict output on local LLMs.
                            payload.Add(new ChatMessage { Role = ChatRole.User, Content = "Mevcut dizindeki dosyaları listele." });
                            payload.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "[TERMINAL_EXEC: Get-ChildItem]" });
                            payload.Add(new ChatMessage { Role = ChatRole.User, Content = "System Outcomes:\nResult of TERMINAL_EXEC 'Get-ChildItem':\n```\nMode  LastWriteTime     Length Name\n-a--- 2026-04-20 10:00   1024 README.md\n```\n\nEvaluate results. If an error persists, fix it via [FILE_WRITE]. If done, give final response." });
                            payload.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "Dizinde şu dosya bulunuyor:\n- README.md (1024 bytes)\n\nBaşka bir işlem yapmamı ister misiniz?" });

                            payload.Add(new ChatMessage { Role = ChatRole.User, Content = "test.txt dosyası oluştur ve içine 'merhaba dünya' yaz." });
                            payload.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "[FILE_WRITE: test.txt | merhaba dünya]" });
                            payload.Add(new ChatMessage { Role = ChatRole.User, Content = "System Outcomes:\nResult of FILE_WRITE 'test.txt': Success (Physical Commitment).\n\nEvaluate results. If an error persists, fix it via [FILE_WRITE]. If done, give final response." });
                            payload.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "test.txt dosyası başarıyla oluşturuldu ve içine 'merhaba dünya' yazıldı." });

                            // ── Layer 3: Deep-Clone actual conversation history ──
                            // We deep-clone to avoid mutating original _history objects
                            foreach (var msg in _history)
                            {
                                payload.Add(new ChatMessage { Role = msg.Role, Content = msg.Content });
                            }

                            // ── Layer 4: User-Role Reinforcement on last message ──
                            // Wrap the final user message with rules reminder so it's in the model's
                            // immediate attention window (recency bias)
                            var lastUserMsg = payload.Last(m => m.Role == ChatRole.User);
                            string memoryBlock = string.IsNullOrWhiteSpace(memoryContext) ? "No previous memory." : memoryContext;
                            lastUserMsg.Content = $"[REMINDER: You MUST use [TERMINAL_EXEC: command], [FILE_READ: path], [FILE_WRITE: path | content] bracket tokens. NEVER use markdown code blocks. Output the bracket tag and STOP.]\n\n[MEMORY]: {memoryBlock}\n\n{lastUserMsg.Content}";

                            responseText = await _ollamaAgent.SendPromptAsync(payload);
                        });

                    // ── Extract and display Thinking Process (For DeepSeek-R1 / Reasoning Models) ──
                    var thinkPattern = new Regex(@"<think>(.*?)</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    var thinkMatches = thinkPattern.Matches(responseText);
                    if (thinkMatches.Count > 0)
                    {
                        foreach (Match m in thinkMatches)
                        {
                            AnsiConsole.MarkupLine($"\n[dim grey]💭 Model Thinking:\n{Markup.Escape(m.Groups[1].Value.Trim())}[/]");
                        }
                        // Strip the thinking tokens so they don't interfere with tool parsing
                        responseText = thinkPattern.Replace(responseText, "").Trim();
                    }

                    var toolResult = await _toolExecution.ExecuteToolsAsync(responseText);
                    if (toolResult.ActionsExecuted)
                    {
                        _history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = responseText });
                        _history.Add(new ChatMessage { Role = ChatRole.User, Content = "System Outcomes:\n" + toolResult.Output + "\nEvaluate results. If an error persists, fix it via [FILE_WRITE]. If done, give final response." });
                        
                        if (toolResult.BudgetExceeded) break;
                        continue; 
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"\n[bold red]✕ Local Agent Error:[/] {Markup.Escape(ex.Message)}");
                AnsiConsole.MarkupLine("[dim yellow]Automatically falling back to Web Agent for this request...[/]");
                fallbackToWeb = true;
            }
        }

        if (fallbackToWeb)
        {
            // Path B: Fallback to Multi-Agent Browser orchestration
            await _agentManager.InitializeAsync();
            responseText = await _agentManager.ProcessUserInputAsync(prompt);
        }

        // 3. Append response to history
        _history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = responseText });

        return responseText;
    }

    /// <summary>
    /// Clears the current conversation history (except the initial system prompt).
    /// </summary>
    public void ClearHistory()
    {
        _history.Clear();
    }

    /// <summary>
    /// Safely tears down the Orchestrator pipeline, saving sessions and closing automated browser instances.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _webAgent.DisposeAsync();
        await _agentManager.DisposeAsync();
        _toolExecution.Dispose();
    }
}
