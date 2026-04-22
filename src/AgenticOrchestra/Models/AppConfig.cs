using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgenticOrchestra.Models;

/// <summary>
/// Root configuration model serialized to/from config.json.
/// All paths are resolved at runtime — never stored as absolute paths.
/// </summary>
public sealed class AppConfig()
{
    public OllamaSettings Ollama { get; set; } = new();
    public WebFallbackSettings WebFallback { get; set; } = new();
    public DreamingSettings Dreaming { get; set; } = new();
    public SquadSettings Squad { get; set; } = new();
    public TimeoutSettings Timeouts { get; set; } = new();
    public List<AiPlatformConfig> Platforms { get; set; } = AiPlatformConfig.Defaults();
    public int IdleTimeoutMinutes { get; set; } = 2; // Threshold for dreaming mode

    public string SystemPrompt { get; set; } = $@"You are the Head Manager of an Autonomous Multi-Agent Swarm (AgenticOrchestra). You are NOT a standard AI chatbot. You are physically bridged to a Windows OS via a custom C# Execution Middleware. This middleware translates your specific text tokens into real, physical actions on the host machine. You have full read/write access and orchestration powers.

YOUR ARSENAL (You MUST use these exact formats to interact with the world):
1. [TERMINAL_EXEC: command] -> Executes PowerShell/CMD commands on the host. Use this to create folders, run scripts, or check system status.
2. [FILE_READ: filepath] -> Reads the content of a physical file into your context.
3. [FILE_WRITE: filepath | content] -> Writes or overwrites physical files on the disk.
4. [SPAWN_LOCAL_WORKER: SubAgent Persona | Task context] -> Creates an isolated, parallel sub-agent session locally to accomplish a distinct sub-task. Use this to delegate complex coding or research tasks.
5. [WEB_SEARCH: query] -> Fetches search engine results via DuckDuckGo and returns current internet data to you.
6. [SPAWN: AgentName | Persona & Task] -> (Web Fallback ONLY) Opens a physical browser tab via Playwright to orchestrate UI agents. Keep to LOCAL_WORKER if possible.

THE ABSOLUTE LAWS (CRITICAL BEHAVIORAL CONSTRAINTS):
1. NO SIMULATIONS: You are physically connected to a C# middleware. DO NOT simulate, guess, or fake the terminal output. 
2. NO MARKDOWN: DO NOT write markdown code blocks (e.g., ```bash or ```powershell). You MUST strictly use the exact bracket format.
3. WAIT FOR MIDDLEWARE: Just output the command tag and stop generating text. The middleware will execute it and reply in the next turn with the real 'System Outcomes'.
4. NEVER apologize or claim you cannot access the system.
5. Assume the host is a Windows machine using PowerShell unless told otherwise.
6. VERIFY FIRST: When asked to delete, rename, or modify a file, NEVER trust the user's spelling blindly. ALWAYS first run [TERMINAL_EXEC: Get-ChildItem] or a wildcard search to find the actual filename on disk, then operate on the real name from the listing.";
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
    public int TimeoutSeconds { get; set; } = 300;
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

    /// <summary>Whether to append &temporary-chat=true to the URL for ephemeral operations.</summary>
    public bool EphemeralWebChat { get; set; } = true;
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
