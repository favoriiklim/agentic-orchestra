using AgenticOrchestra.Models;

namespace AgenticOrchestra.Services;

/// <summary>
/// Coordinates the hybrid AI pipeline.
/// Attempts to use the local Ollama instance first.
/// If unavailable, it falls back to the Playwright web automation agent.
/// </summary>
public sealed class OrchestratorService
{
    private readonly AppConfig _config;
    private readonly OllamaAgent _ollamaAgent;
    private readonly PlaywrightWebAgent _webAgent;
    
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
        _history = new List<ChatMessage>
        {
            new ChatMessage { Role = ChatRole.System, Content = config.SystemPrompt }
        };
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
        // 1. Add user prompt to history
        _history.Add(new ChatMessage { Role = ChatRole.User, Content = prompt });

        string responseText;

        // 2. Fallback Logic Pipeline
        IsUsingLocalAgent = await _ollamaAgent.IsAvailableAsync();

        if (IsUsingLocalAgent)
        {
            // Path A: Local execution via Ollama
            responseText = await _ollamaAgent.SendPromptAsync(_history);
        }
        else
        {
            // Path B: Fallback to Browser automation
            // Note: History is not perfectly maintained in the browser fallback MVP
            // since the web session implicitly maintains its own state in the thread.
            // We just send the latest prompt to keep it simple.
            responseText = await _webAgent.SendPromptAsync(prompt);
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
        _history.Add(new ChatMessage { Role = ChatRole.System, Content = _config.SystemPrompt });
    }
}
