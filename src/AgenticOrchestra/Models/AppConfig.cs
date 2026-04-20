namespace AgenticOrchestra.Models;

/// <summary>
/// Root configuration model serialized to/from config.json.
/// All paths are resolved at runtime — never stored as absolute paths.
/// </summary>
public sealed class AppConfig()
{
    public OllamaSettings Ollama { get; set; } = new();
    public WebFallbackSettings WebFallback { get; set; } = new();
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
5. Assume the host is a Windows machine using PowerShell unless told otherwise.";
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
}
