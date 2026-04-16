namespace AgenticOrchestra.Models;

/// <summary>
/// Root configuration model serialized to/from config.json.
/// All paths are resolved at runtime — never stored as absolute paths.
/// </summary>
public sealed class AppConfig
{
    public OllamaSettings Ollama { get; set; } = new();
    public WebFallbackSettings WebFallback { get; set; } = new();
    public string SystemPrompt { get; set; } = "You are a helpful AI assistant. Be concise and precise in your answers.";
}

/// <summary>
/// Settings for the local Ollama LLM instance.
/// </summary>
public sealed class OllamaSettings
{
    /// <summary>Base URL for the Ollama API. Default: http://localhost:11434</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Model name to use for chat completions (e.g. llama3.2, mistral, phi3:mini).</summary>
    public string Model { get; set; } = "llama3.2";

    /// <summary>Maximum seconds to wait for a response before timing out.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Settings for the Playwright web automation fallback.
/// </summary>
public sealed class WebFallbackSettings
{
    /// <summary>Target URL for the web-based AI platform.</summary>
    public string TargetUrl { get; set; } = "https://gemini.google.com/app";

    /// <summary>Run the browser in headless mode. Set to false to see the browser window.</summary>
    public bool Headless { get; set; } = false;

    /// <summary>Placeholder text used to locate the prompt input field via accessibility locator.</summary>
    public string InputPlaceholder { get; set; } = "Enter a prompt here";
}
