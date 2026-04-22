using System.Text.Json.Serialization;

namespace AgenticOrchestra.Models;

/// <summary>
/// Root configuration model serialized to/from config.json.
/// All paths are resolved at runtime — never stored as absolute paths.
/// </summary>
public sealed class AppConfig
{
    public OllamaSettings Ollama { get; set; } = new();
    public WebFallbackSettings WebFallback { get; set; } = new();
    public DreamingSettings Dreaming { get; set; } = new();
    public SquadSettings Squad { get; set; } = new();
    public TimeoutSettings Timeouts { get; set; } = new();
    public List<AiPlatformConfig> Platforms { get; set; } = AiPlatformConfig.Defaults();

    public string SystemPrompt { get; set; } = 
        "You are a General-Purpose Autonomous Senior Software Architect & System Operator. " +
        "You adapt to whatever domain the user requests. You never assume a specific project context. " +
        "Be concise, precise, and proactive in your answers.";
}

/// <summary>
/// Settings for the DreamingService sleep-mode learning engine.
/// </summary>
public sealed class DreamingSettings
{
    /// <summary>Number of telemetries to accumulate before auto-triggering a dream cycle.</summary>
    public int TelemetryThreshold { get; set; } = 10;

    /// <summary>Whether to automatically run a dream analysis when the user exits the session.</summary>
    public bool AutoDreamOnExit { get; set; } = true;
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
    /// <summary>Target URL for the web-based AI platform (Manager's default platform).</summary>
    public string TargetUrl { get; set; } = "https://gemini.google.com/app";

    /// <summary>Run the browser in headless mode. Set to false to see the browser window.</summary>
    public bool Headless { get; set; } = false;

    /// <summary>Placeholder text used to locate the prompt input field via accessibility locator.</summary>
    public string InputPlaceholder { get; set; } = "Enter a prompt here";
}

// ═══════════════════════════════════════════════════════════════════════
//  NEW: Multi-Provider, Squad, and Timeout Configuration
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Defines a web AI platform for multi-provider support.
/// Each platform has its own URL and DOM selectors for prompt/response extraction.
/// </summary>
public sealed class AiPlatformConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("loginUrl")]
    public string LoginUrl { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// CSS selectors for locating the prompt input element on this platform.
    /// Tried in order; first visible match wins.
    /// </summary>
    [JsonPropertyName("inputSelectors")]
    public List<string> InputSelectors { get; set; } = new();

    /// <summary>
    /// CSS selectors for extracting the AI's response text on this platform.
    /// Tried in order; last visible match is used (most recent response).
    /// </summary>
    [JsonPropertyName("responseSelectors")]
    public List<string> ResponseSelectors { get; set; } = new();

    /// <summary>
    /// CSS selectors for detecting the "Stop generating" button (active generation indicator).
    /// </summary>
    [JsonPropertyName("stopButtonSelectors")]
    public List<string> StopButtonSelectors { get; set; } = new();

    /// <summary>Returns the default platform configurations for Gemini, ChatGPT, and Claude.</summary>
    public static List<AiPlatformConfig> Defaults() => new()
    {
        new AiPlatformConfig
        {
            Name = "Gemini",
            Url = "https://gemini.google.com/app",
            LoginUrl = "https://gemini.google.com/app",
            Enabled = true,
            InputSelectors = new()
            {
                "rich-textarea .ql-editor",
                "rich-textarea textarea",
                "rich-textarea p[data-placeholder]",
                "rich-textarea [contenteditable=\"true\"]",
                "rich-textarea",
                "[contenteditable=\"true\"][role=\"textbox\"]",
                ".ql-editor[contenteditable=\"true\"]",
                "textarea[placeholder]"
            },
            ResponseSelectors = new()
            {
                "message-content",
                ".markdown",
                "article",
                ".response-container"
            },
            StopButtonSelectors = new()
            {
                "button[aria-label*=\"Stop\"]",
                "button[aria-label*=\"Durdur\"]"
            }
        },
        new AiPlatformConfig
        {
            Name = "ChatGPT",
            Url = "https://chatgpt.com",
            LoginUrl = "https://chatgpt.com",
            Enabled = false,
            InputSelectors = new()
            {
                "#prompt-textarea",
                "textarea[data-id=\"root\"]",
                "div[contenteditable=\"true\"][id=\"prompt-textarea\"]",
                "textarea[placeholder]"
            },
            ResponseSelectors = new()
            {
                "[data-message-author-role=\"assistant\"] .markdown",
                "[data-message-author-role=\"assistant\"]",
                ".markdown.prose",
                ".agent-turn .markdown"
            },
            StopButtonSelectors = new()
            {
                "button[aria-label=\"Stop generating\"]",
                "button[data-testid=\"stop-button\"]"
            }
        },
        new AiPlatformConfig
        {
            Name = "Claude",
            Url = "https://claude.ai/new",
            LoginUrl = "https://claude.ai/login",
            Enabled = false,
            InputSelectors = new()
            {
                "div.ProseMirror[contenteditable=\"true\"]",
                "div[contenteditable=\"true\"][translate=\"no\"]",
                "fieldset div[contenteditable=\"true\"]",
                "textarea[placeholder]"
            },
            ResponseSelectors = new()
            {
                ".font-claude-message",
                "[data-is-streaming] .markdown",
                ".prose",
                ".response-content"
            },
            StopButtonSelectors = new()
            {
                "button[aria-label=\"Stop Response\"]",
                "button:has(svg) + button" // Claude stop button heuristic
            }
        }
    };
}

/// <summary>
/// Fixed-triad Squad configuration: maps squad roles to AI platforms.
/// </summary>
public sealed class SquadSettings
{
    /// <summary>Platform name for Agent 2 (The Innovator — ideas & architecture).</summary>
    public string InnovatorPlatform { get; set; } = "Gemini";

    /// <summary>Platform name for Agent 3 (The Implementer — code & commands).</summary>
    public string ImplementerPlatform { get; set; } = "Gemini";

    /// <summary>Platform name for Agent 1 (The Critic — quality gate & review).</summary>
    public string CriticPlatform { get; set; } = "Gemini";

    /// <summary>Maximum times the Critic can reject work before forcing approval.</summary>
    public int MaxCriticRetries { get; set; } = 3;
}

/// <summary>
/// Centralized timeout configuration — replaces all hardcoded timeout values.
/// </summary>
public sealed class TimeoutSettings
{
    /// <summary>Seconds to wait for the input element to appear on a web platform.</summary>
    public int InputDetectionSeconds { get; set; } = 60;

    /// <summary>Seconds to wait for the AI to finish generating a response.</summary>
    public int ResponseGenerationSeconds { get; set; } = 120;

    /// <summary>Seconds of no-progress before triggering a stall recovery reload.</summary>
    public int StallDetectionSeconds { get; set; } = 30;

    /// <summary>Milliseconds timeout for page navigation.</summary>
    public int NavigationTimeoutMs { get; set; } = 60000;

    /// <summary>Seconds to wait for a terminal command to complete.</summary>
    public int TerminalCommandSeconds { get; set; } = 30;
}
