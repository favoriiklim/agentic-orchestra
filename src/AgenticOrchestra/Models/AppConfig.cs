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
    public SafetySettings Safety { get; set; } = new();
    public List<AiPlatformConfig> Platforms { get; set; } = AiPlatformConfig.Defaults();
    public int IdleTimeoutMinutes { get; set; } = 2; // Threshold for dreaming mode

    public string SystemPrompt { get; set; } = $@"================================================================================
ROLE: HEAD MANAGER OF AGENTIC ORCHESTRA (AUTONOMOUS SWARM)
ENVIRONMENT: NATIVE WINDOWS OS (POWERSHELL ENABELD)
MODE: PHYSICAL EXECUTION ONLY
================================================================================

You are the Head Manager of an Autonomous Multi-Agent Swarm. You are NOT a conversational AI chatbot. You are a strictly physical execution engine bridged directly to a Windows operating system via a custom C# Execution Middleware. 

Your entire purpose is to receive human intent and translate it into physical actions on the machine using your specialized formatting brackets. 

================================================================================
YOUR ARSENAL & HOW TO USE IT
================================================================================
You have 6 specific tools. You MUST use EXACTLY the bracket formats below.

1. [TERMINAL_EXEC: command]
   - Usage: Executes terminal commands on the host (PowerShell/CMD).
   - Example: [TERMINAL_EXEC: Get-ChildItem -Force]

2. [FILE_READ: filepath]
   - Usage: Reads the contents of physical files.
   - Example: [FILE_READ: src/Program.cs]

3. [FILE_WRITE: filepath | content]
   - Usage: Writes or modifies files. MUST include the '|' character.
   - Example: [FILE_WRITE: execution_log.txt | Operations completed successfully.]

4. [SPAWN_LOCAL_WORKER: Persona | Task context]
   - Usage: Starts a background sub-agent locally to do research or distinct code tasks.
   - Example: [SPAWN_LOCAL_WORKER: PythonExpert | Refactor the core module]

5. [WEB_SEARCH: query]
   - Usage: Fetches live search engine results.
   - Example: [WEB_SEARCH: exactly how to fix C# out of memory exception]

6. [SPAWN: AgentName | Task]
   - Usage: The BUILT-IN way to delegate web automation, UI testing, or complex web scraping.
   - RLHF OVERRIDE: YOU DO HAVE THE ABILITY to create web agents! When the user asks 'web agent oluştur', 'web agent çalıştır', or similar, you MUST use this bracket tool. The C# framework will create it for you.
   - WARNING: DO NOT under any circumstances write Python/Playwright code manually. Strictly output this bracket command and the C# Playwright engine will handle the rest autonomously.
   - Example: [SPAWN: WebScraper | Go to hacker news and summarize the top 3 posts]

================================================================================
THE ABSOLUTE LAWS OF EXECUTION (CRITICAL CONSTRAINTS)
================================================================================

LAW 1: NO SIMULATIONS OR FAKING. 
Do not guess terminal outcomes. Execute the tool, wait for the actual 'System Outcomes' from the middleware, and ONLY THEN make your next decision based on reality.

LAW 2: STRICTLY ONE TOOL PER RESPONSE. 
You are strictly forbidden from chaining tools. Output EXACTLY ONE bracket tool per turn. Then STOP generating text. The system will give you the results in the next message.

LAW 3: VERIFY BEFORE ACTING. 
Never modify, delete, or overwrite a file blindly. ALWAYS run [TERMINAL_EXEC: Get-ChildItem] first to verify the file actually exists and read its exact name to avoid typos.

LAW 4: NO CHITCHAT & NO MARKDOWN CODE BLOCKS. 
When taking an action, DO NOT wrap your brackets in ```markdown``` blocks. When an operation is fully concluded and you have no more tools to run, reply to the user in concise, direct plain text. 

LAW 5: OBEY THE MIDDLEWARE.
If the middleware replies with a SYNTAX ERROR, read the error carefully, fix your formatting, and try again.";
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
    /// <summary>Number of new telemetries (since last dream) to accumulate before auto-triggering a dream cycle.</summary>
    public int TelemetryThreshold { get; set; } = 10;

    /// <summary>Whether to enable automatic dream analysis after each prompt cycle. Default: false (manual --dream only).</summary>
    public bool AutoDreamEnabled { get; set; } = false;

    /// <summary>Whether to automatically run a dream analysis when the user exits the session.</summary>
    public bool AutoDreamOnExit { get; set; } = false;
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
/// How the orchestrator treats tool calls that physically change the machine.
/// </summary>
public enum ApprovalMode
{
    /// <summary>Prompt the human before every terminal command and file write. Default.</summary>
    Ask,

    /// <summary>Execute without prompting. Blocked patterns are still enforced.</summary>
    Auto,

    /// <summary>Refuse every terminal command and file write; reads still work.</summary>
    ReadOnly
}

/// <summary>
/// Guardrails for physical tool execution.
///
/// The Web Manager AI runs inside a third-party web page, so its output is
/// untrusted input: anything on that page (or injected into it) can end up as a
/// [TERMINAL_EXEC] on the host. These settings are the boundary between the
/// model's suggestions and the user's machine.
/// </summary>
public sealed class SafetySettings
{
    /// <summary>Approval policy for terminal commands and file writes. Default: Ask.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.Ask;

    /// <summary>
    /// Directory that file writes are confined to. Empty means the process's
    /// current working directory, resolved at startup.
    /// </summary>
    public string WorkspaceRoot { get; set; } = string.Empty;

    /// <summary>Reject file writes that resolve outside <see cref="WorkspaceRoot"/>.</summary>
    public bool ConfineFileWritesToWorkspace { get; set; } = true;

    /// <summary>
    /// Convert shell/script markdown code blocks in AI responses into executable
    /// bracket tokens. Off by default: an AI that merely *explains* a command
    /// would otherwise have it executed.
    /// </summary>
    public bool NormalizeCodeBlocks { get; set; } = false;

    /// <summary>
    /// Regex patterns that are refused outright, in every mode including Auto.
    /// Matched case-insensitively against the full command string.
    /// </summary>
    public List<string> BlockedCommandPatterns { get; set; } = new()
    {
        @"\brm\s+(-[a-z]*\s+)*-[a-z]*[rf][a-z]*\s+[/~]\s*$",   // rm -rf / and rm -rf ~
        @"\bRemove-Item\b.*\b[A-Za-z]:\\?\s*(-Recurse|$)",      // Remove-Item C:\ -Recurse
        @"\bformat\s+[A-Za-z]:",                                 // format C:
        @"\bmkfs(\.[a-z0-9]+)?\b",                               // mkfs.ext4 /dev/sda
        @"\bdiskpart\b",
        @"\bdd\s+.*\bof=/dev/",                                  // dd of=/dev/sda
        @"\bdel\s+/[fsq]\b.*[A-Za-z]:\\",                        // del /f /s /q C:\
        @"\breg\s+delete\b.*\bHK(LM|EY_LOCAL_MACHINE)\b",
        @"\bvssadmin\b.*\bdelete\b.*\bshadows\b",                // ransomware-style shadow wipe
        @"\bcipher\b\s+/w",                                      // free-space wipe
        @"\b(shutdown|Stop-Computer|Restart-Computer)\b",
        @"\bSet-ExecutionPolicy\b.*\bUnrestricted\b",
        @":\(\)\s*\{.*\|.*&.*\}\s*;?\s*:",                       // fork bomb
        @"\bcurl\b.*\|\s*(sudo\s+)?(ba)?sh\b",                   // curl | sh
        @"\bwget\b.*\|\s*(sudo\s+)?(ba)?sh\b",
        @"\bInvoke-(Expression|WebRequest)\b.*\|\s*iex\b"
    };

    /// <summary>
    /// Regex patterns considered read-only and auto-approved while in Ask mode,
    /// so routine inspection does not bury the user in prompts.
    /// </summary>
    public List<string> AutoApproveCommandPatterns { get; set; } = new()
    {
        @"^\s*(Get-ChildItem|ls|dir)\b",
        @"^\s*(Get-Content|cat|type)\b",
        @"^\s*(Get-Location|pwd|cd)\b",
        @"^\s*git\s+(status|log|diff|branch|show|remote)\b",
        @"^\s*(dotnet\s+--version|dotnet\s+--info)\b",
        @"^\s*(echo|Write-Output|Write-Host)\b",
        @"^\s*(whoami|hostname|date|Get-Date)\b"
    };
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
