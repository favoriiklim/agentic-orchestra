using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using AgenticOrchestra.Models;

namespace AgenticOrchestra.Services;

/// <summary>
/// Result of a tool execution pass.
/// </summary>
public record ToolExecutionResult(string Output, bool ActionsExecuted, bool BudgetExceeded);

/// <summary>
/// Shared service to parse and execute physical tools (Terminal, File Read/Write).
/// </summary>
public sealed class ToolExecutionService : IDisposable
{
    private readonly AppConfig _config;
    private readonly NativeFileService _fileService;
    private readonly NativeTerminalService _terminalService;
    private readonly HttpClient _httpClient;
    private AgentManagerService? _agentManager; // Injected later to avoid circular dependency
    private int _consecutiveFailures = 0;
    private const int MaxRetryBudget = 5;

    public ToolExecutionService(AppConfig config)
    {
        _config = config;
        _fileService = new NativeFileService();
        _terminalService = new NativeTerminalService();
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
    }

    /// <summary>
    /// Late-binds the AgentManager to break circular dependency (Orchestrator -> ToolExec -> AgentManager -> ToolExec).
    /// </summary>
    public void SetAgentManager(AgentManagerService agentManager)
    {
        _agentManager = agentManager;
    }

    /// <summary>
    /// Parses the AI response for [TERMINAL_EXEC], [FILE_READ], and [FILE_WRITE] tokens
    /// and executes them physically.
    /// Also normalizes markdown code blocks into bracket format before parsing.
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteToolsAsync(string aiResponse)
    {
        // ── Step 0: Normalize markdown code blocks into bracket tokens ──
        // This makes the system model-agnostic: works with both bracket tokens AND markdown output
        aiResponse = NormalizeResponse(aiResponse);

        bool actionExecuted = false;
        var loopFeedBuilder = new StringBuilder();
        var executedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Dedup guard

        // 1. Parse File Reads (deduplicated)
        var readMatches = Regex.Matches(aiResponse, @"\[FILE_READ:\s*([^\]]+)\]");
        foreach (Match m in readMatches)
        {
            var path = m.Groups[1].Value.Trim();
            var dedupKey = $"READ:{path}";
            if (!executedCommands.Add(dedupKey)) continue; // Skip duplicate
            AnsiConsole.MarkupLine($"[dim cyan]Native Agent reading:[/] {Markup.Escape(path)}");
            var content = _fileService.ReadFile(path);
            loopFeedBuilder.AppendLine($"Result of FILE_READ '{path}':\n```\n{content}\n```\n");
            actionExecuted = true;
        }

        // 2. Parse Terminal Commands
        var termMatches = Regex.Matches(aiResponse, @"\[TERMINAL_EXEC:\s*([^\]]+)\]");
        foreach (Match m in termMatches)
        {
            var cmd = m.Groups[1].Value.Trim();
            var dedupKey = $"EXEC:{cmd}";
            if (!executedCommands.Add(dedupKey)) continue; // Skip duplicate
            var result = await _terminalService.ExecuteCommandAsync(cmd);
            
            // Heuristic error detection
            bool isError = result.Contains("Error", StringComparison.OrdinalIgnoreCase) || 
                           result.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
                           result.Contains("Exception", StringComparison.OrdinalIgnoreCase);

            if (isError)
            {
                _consecutiveFailures++;
                AnsiConsole.MarkupLine($"[bold red]✕ Command failed ({_consecutiveFailures}/{MaxRetryBudget}).[/]");
            }
            else
            {
                _consecutiveFailures = 0; 
            }

            loopFeedBuilder.AppendLine($"Result of TERMINAL_EXEC '{cmd}':\n```\n{result}\n```\n");
            actionExecuted = true;

            if (_consecutiveFailures >= MaxRetryBudget)
            {
                AnsiConsole.MarkupLine("[bold red]FATAL: Bug-fix budget exhausted.[/]");
                loopFeedBuilder.AppendLine("\nERROR: Bug-fix budget exceeded (5 retries). Please intervene manually.");
                return new ToolExecutionResult(loopFeedBuilder.ToString(), true, true);
            }
        }

        // 3. Parse File Writes
        var writeMatches = Regex.Matches(aiResponse, @"\[FILE_WRITE:\s*(?<path>.*?)\s*\|\s*(?<content>.*?)\]", RegexOptions.Singleline);
        foreach (Match m in writeMatches)
        {
            var path = m.Groups["path"].Value.Trim();
            var content = m.Groups["content"].Value.Trim();
            var dedupKey = $"WRITE:{path}";
            if (!executedCommands.Add(dedupKey)) continue; // Skip duplicate
            var result = _fileService.WriteFile(path, content);
            if (result.Contains("Success"))
            {
                AnsiConsole.MarkupLine($"[bold green][[SUCCESS]][/] File Physically Written to: {Markup.Escape(path)}");
                loopFeedBuilder.AppendLine($"Result of FILE_WRITE '{path}': Success (Physical Commitment).");
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold red]✕ Physical Write Failed for {Markup.Escape(path)}:[/] {Markup.Escape(result)}");
                loopFeedBuilder.AppendLine($"Result of FILE_WRITE '{path}': FAILED - {result}");
            }
            
            actionExecuted = true;
        }

        // 4. Parse Web Searches
        var searchMatches = Regex.Matches(aiResponse, @"\[WEB_SEARCH:\s*([^\]]+)\]");
        foreach (Match m in searchMatches)
        {
            var query = m.Groups[1].Value.Trim();
            AnsiConsole.Status().Start($"Searching web for: [bold]{Markup.Escape(query)}[/]...", async ctx => 
            {
                var searchResult = await PerformWebSearchAsync(query);
                loopFeedBuilder.AppendLine($"Result of WEB_SEARCH '{query}':\n```\n{searchResult}\n```\n");
            }).Wait();
            actionExecuted = true;
        }

        // 5. Parse Local Worker Spawning
        var spawnMatches = Regex.Matches(aiResponse, @"\[SPAWN_LOCAL_WORKER:\s*(?<persona>.*?)\s*\|\s*(?<task>.*?)\]", RegexOptions.Singleline);
        foreach (Match m in spawnMatches)
        {
            var persona = m.Groups["persona"].Value.Trim();
            var taskContext = m.Groups["task"].Value.Trim();

            AnsiConsole.MarkupLine($"[bold magenta]🏗️ Spawning Local Worker:[/] [dim]{Markup.Escape(persona)}[/]");
            
            var workerResult = await SpawnLocalWorkerAsync(persona, taskContext);
            loopFeedBuilder.AppendLine($"Result of LOCAL_WORKER (Persona: {persona}):\n{workerResult}\n");
            
            actionExecuted = true;
        }

        // 6. Parse Web Agent Spawning [SPAWN: AgentName | Task] (Playwright bridge)
        var webSpawnMatches = Regex.Matches(aiResponse, @"\[SPAWN:\s*(?<name>[^\|]+)\|\s*(?<task>.+?)\]", RegexOptions.Singleline);
        foreach (Match m in webSpawnMatches)
        {
            var agentName = m.Groups["name"].Value.Trim();
            var taskInstruction = m.Groups["task"].Value.Trim();

            if (_agentManager != null)
            {
                AnsiConsole.MarkupLine($"[bold cyan]⚡ Spawning Web Agent:[/] [dim]{Markup.Escape(agentName)}[/]");
                
                try
                {
                    await _agentManager.InitializeAsync();
                    var webResult = await _agentManager.ProcessUserInputAsync(
                        $"[SPAWN: {agentName} | {taskInstruction}]"
                    );
                    loopFeedBuilder.AppendLine($"Result of SPAWN '{agentName}':\n{webResult}\n");
                }
                catch (Exception ex)
                {
                    loopFeedBuilder.AppendLine($"Result of SPAWN '{agentName}': FAILED - {ex.Message}\n");
                    AnsiConsole.MarkupLine($"[bold red]✕ Web Agent Spawn Failed:[/] {Markup.Escape(ex.Message)}");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold yellow]⚠ SPAWN requested but Web Agent unavailable. Falling back to LOCAL_WORKER...[/]");
                var fallbackResult = await SpawnLocalWorkerAsync(agentName, taskInstruction);
                loopFeedBuilder.AppendLine($"Result of SPAWN->LOCAL_WORKER '{agentName}':\n{fallbackResult}\n");
            }
            actionExecuted = true;
        }

        return new ToolExecutionResult(loopFeedBuilder.ToString(), actionExecuted, false);
    }

    private async Task<string> PerformWebSearchAsync(string query)
    {
        try 
        {
            var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
            var html = await _httpClient.GetStringAsync(url);
            
            // Extract result snippets using simple regex (titles and snippets)
            var matches = Regex.Matches(html, @"<a class=""result-link""[^>]*>(.*?)</a>.*?<td class=""result-snippet"">(.*?)</td>", RegexOptions.Singleline);
            
            var results = new StringBuilder();
            int count = 0;
            foreach (Match m in matches)
            {
                if (count >= 4) break;
                var title = Regex.Replace(m.Groups[1].Value, "<.*?>", "").Trim();
                var snippet = Regex.Replace(m.Groups[2].Value, "<.*?>", "").Trim();
                results.AppendLine($"[{count+1}] {title}\n    {snippet}\n");
                count++;
            }

            return results.Length > 0 ? results.ToString() : "No search results found. DuckDuckGo Lite might be rate-limiting.";
        }
        catch (Exception ex)
        {
            return $"Web Search Failed: {ex.Message}";
        }
    }

    private async Task<string> SpawnLocalWorkerAsync(string persona, string task)
    {
        try
        {
            var workerAgent = new OllamaAgent(_config);
            var payload = new List<ChatMessage>
            {
                new ChatMessage { Role = ChatRole.System, Content = $"You are a specialized sub-agent. Persona: {persona}\nTask: Executing a specific instruction for the Head Manager.\nOutput only the result or final answer. Keep it technical and concise." },
                new ChatMessage { Role = ChatRole.User, Content = task }
            };

            var response = await workerAgent.SendPromptAsync(payload);
            return response;
        }
        catch (Exception ex)
        {
            return $"Worker Spawn Failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Pre-processes the AI response to convert markdown code blocks (```bash, ```powershell, etc.)
    /// into bracket tokens that our existing Regex can parse.
    /// This makes the system model-agnostic — it doesn't matter if the LLM outputs [TERMINAL_EXEC: ls]
    /// or ```bash\nls\n``` — both will be executed.
    /// </summary>
    private static string NormalizeResponse(string aiResponse)
    {
        // Find all markdown code blocks with shell/script language hints
        // Use [\r\n]+ instead of \n to handle Windows line endings
        var codeBlockPattern = new Regex(
            @"```(?:bash|powershell|cmd|shell|sh|ps1|ps|python|py)\s*[\r\n]+(.*?)[\r\n]*```",
            RegexOptions.Singleline | RegexOptions.IgnoreCase
        );

        var normalized = new StringBuilder(aiResponse);
        var matches = codeBlockPattern.Matches(aiResponse);

        if (matches.Count > 0)
        {
            AnsiConsole.MarkupLine($"\n[bold yellow]⚙ Normalizer:[/] Detected {matches.Count} code block(s) in AI response. Converting to bracket tokens...");
        }

        // Process in reverse order so string indices don't shift
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var blockContent = match.Groups[1].Value;
            var convertedTokens = new StringBuilder();

            // Split on both \r\n and \n
            var lines = blockContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                // Skip empty lines, comments, and simulated output lines
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith('#')) continue;
                if (line.StartsWith("//")) continue;

                // Strip leading $ or > prompt characters
                if (line.StartsWith("$ ")) line = line[2..];
                else if (line.StartsWith("> ")) line = line[2..];

                // Skip Python-specific lines (import, print, with open, etc.)
                if (line.StartsWith("import ") || line.StartsWith("from ") ||
                    line.StartsWith("print(") || line.StartsWith("with ") ||
                    line.Contains("open(") || line.Contains("os.listdir"))
                {
                    continue;
                }

                // Detect echo/write redirect pattern: echo "content" > file.txt → [FILE_WRITE]
                var echoRedirect = Regex.Match(line, @"^echo\s+""(.*?)""\s*>\s*(.+)$", RegexOptions.IgnoreCase);
                if (echoRedirect.Success)
                {
                    var content = echoRedirect.Groups[1].Value.Trim();
                    var path = echoRedirect.Groups[2].Value.Trim();
                    convertedTokens.AppendLine($"[FILE_WRITE: {path} | {content}]");
                    AnsiConsole.MarkupLine($"[dim green]  → Converted echo redirect to FILE_WRITE: {Markup.Escape(path)}[/]");
                    continue;
                }

                // Detect Set-Content / Out-File PowerShell patterns
                var setContent = Regex.Match(line, @"^(?:Set-Content|Out-File)\s+-Path\s+""?(.*?)""?\s+-Value\s+""?(.*?)""?\s*$", RegexOptions.IgnoreCase);
                if (setContent.Success)
                {
                    var path = setContent.Groups[1].Value.Trim();
                    var content = setContent.Groups[2].Value.Trim();
                    convertedTokens.AppendLine($"[FILE_WRITE: {path} | {content}]");
                    AnsiConsole.MarkupLine($"[dim green]  → Converted Set-Content to FILE_WRITE: {Markup.Escape(path)}[/]");
                    continue;
                }

                // Detect cat/type file read patterns
                var catRead = Regex.Match(line, @"^(?:cat|type|Get-Content)\s+(.+)$", RegexOptions.IgnoreCase);
                if (catRead.Success)
                {
                    var path = catRead.Groups[1].Value.Trim().Trim('"', '\'');
                    convertedTokens.AppendLine($"[FILE_READ: {path}]");
                    AnsiConsole.MarkupLine($"[dim green]  → Converted to FILE_READ: {Markup.Escape(path)}[/]");
                    continue;
                }

                // Everything else → TERMINAL_EXEC
                convertedTokens.AppendLine($"[TERMINAL_EXEC: {line}]");
                AnsiConsole.MarkupLine($"[dim green]  → Converted to TERMINAL_EXEC: {Markup.Escape(line)}[/]");
            }

            // Replace the markdown block with the converted bracket tokens
            if (convertedTokens.Length > 0)
            {
                normalized.Remove(match.Index, match.Length);
                normalized.Insert(match.Index, convertedTokens.ToString());
            }
        }

        return normalized.ToString();
    }

    public void ResetBudget()
    {
        _consecutiveFailures = 0;
    }

    public void Dispose()
    {
        _terminalService.Dispose();
    }
}
