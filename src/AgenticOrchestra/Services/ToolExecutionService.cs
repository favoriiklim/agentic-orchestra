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
    private readonly SafetyGuard _guard;
    private readonly HttpClient _httpClient;
    private AgentManagerService? _agentManager;
    private PlaywrightWebAgent? _webAgent;
    private int _consecutiveFailures = 0;
    private const int MaxRetryBudget = 5;

    public ToolExecutionService(AppConfig config, SafetyGuard? guard = null)
    {
        _config = config;
        _fileService = new NativeFileService();
        _terminalService = new NativeTerminalService();
        _guard = guard ?? new SafetyGuard(config.Safety);
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
    /// Injects the web agent for direct SPAWN dispatch (avoids re-entry through AgentManager).
    /// </summary>
    public void SetWebAgent(PlaywrightWebAgent webAgent)
    {
        _webAgent = webAgent;
    }

    /// <summary>
    /// Parses the AI response for [TERMINAL_EXEC], [FILE_READ], and [FILE_WRITE] tokens
    /// and executes them physically.
    /// Also normalizes markdown code blocks into bracket format before parsing.
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteToolsAsync(string aiResponse, ManagerTelemetry telemetry, CancellationToken ct = default)
    {
        // ── Step 0: Optionally normalize markdown code blocks into bracket tokens ──
        // Makes the system model-agnostic, but also turns a code block the AI merely
        // *explained* into something we execute — so it is opt-in via config.
        if (_guard.NormalizeCodeBlocks)
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

            // ── Safety gate ──
            if (!Authorize(ToolKind.Terminal, cmd, _guard.EvaluateCommand(cmd), out var termRefusal))
            {
                loopFeedBuilder.AppendLine($"Result of TERMINAL_EXEC '{cmd}': REFUSED - {termRefusal}");
                actionExecuted = true;
                continue;
            }

            var result = await _terminalService.ExecuteCommandAsync(cmd, ct);
            
            // Heuristic error detection
            bool isError = result.Contains("__EXITCODE__:1") || result.Contains("Error: Command timed out or was interrupted");

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

        ct.ThrowIfCancellationRequested();

        // 3. Parse File Writes
        var writeMatches = Regex.Matches(aiResponse, @"\[FILE_WRITE:\s*(?<path>[^|]+?)\|\s*(?<content>[\s\S]*?)\s*\](?=\s*\r?\n|\s*$)", RegexOptions.Singleline);
        foreach (Match m in writeMatches)
        {
            var path = m.Groups["path"].Value.Trim();
            var content = m.Groups["content"].Value.Trim();
            var dedupKey = $"WRITE:{path}";
            if (!executedCommands.Add(dedupKey)) continue; // Skip duplicate

            // ── Safety gate ──
            var writeVerdict = _guard.EvaluateFileWrite(path);
            if (!Authorize(ToolKind.FileWrite, path, writeVerdict, out var writeRefusal, content))
            {
                loopFeedBuilder.AppendLine($"Result of FILE_WRITE '{path}': REFUSED - {writeRefusal}");
                actionExecuted = true;
                continue;
            }

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
            ct.ThrowIfCancellationRequested();
            var query = m.Groups[1].Value.Trim();
            AnsiConsole.MarkupLine($"[dim cyan]Searching web for:[/] {Markup.Escape(query)}");
            var searchResult = await PerformWebSearchAsync(query, ct);
            loopFeedBuilder.AppendLine($"Result of WEB_SEARCH '{query}':\n```\n{searchResult}\n```\n");
            actionExecuted = true;
        }

        ct.ThrowIfCancellationRequested();

        // 5. Parse Local Worker Spawning
        var spawnMatches = Regex.Matches(aiResponse, @"\[SPAWN_LOCAL_WORKER:\s*(?<persona>.*?)\s*\|\s*(?<task>.*?)\]", RegexOptions.Singleline);
        foreach (Match m in spawnMatches)
        {
            ct.ThrowIfCancellationRequested();
            var persona = m.Groups["persona"].Value.Trim();
            var taskContext = m.Groups["task"].Value.Trim();

            AnsiConsole.MarkupLine($"[bold magenta]🏗️ Spawning Local Worker:[/] [dim]{Markup.Escape(persona)}[/]");
            
            var workerResult = await SpawnLocalWorkerAsync(persona, taskContext, ct);
            loopFeedBuilder.AppendLine($"Result of LOCAL_WORKER (Persona: {persona}):\n{workerResult}\n");
            
            actionExecuted = true;
        }

        ct.ThrowIfCancellationRequested();

        // 5.5 Process [SPAWN_SQUAD] — Squad Triad Deployment
        var squadMatches = Regex.Matches(aiResponse, @"\[SPAWN_SQUAD:\s*(.+?)\]", RegexOptions.Singleline);
        foreach (Match m in squadMatches)
        {
            var squadTask = m.Groups[1].Value.Trim();
            if (squadTask.EndsWith("]")) squadTask = squadTask[..^1].Trim();

            if (_agentManager != null)
            {
                var squadResult = await _agentManager.RunSquadProxyAsync(squadTask, telemetry, ct);
                loopFeedBuilder.AppendLine("System Knowledge Drop (Squad Output):");
                loopFeedBuilder.AppendLine(squadResult);
                actionExecuted = true;
            }
        }

        // 6. Parse Web Agent Spawning [SPAWN: AgentName | Task]
        // Direct dispatch to a worker tab — no re-entry through AgentManager
        var webSpawnMatches = Regex.Matches(aiResponse, @"\[SPAWN:\s*(?<name>[^\|]+)\|\s*(?<task>.+?)\]", RegexOptions.Singleline);
        foreach (Match m in webSpawnMatches)
        {
            ct.ThrowIfCancellationRequested();
            var agentName = m.Groups["name"].Value.Trim();
            var taskInstruction = m.Groups["task"].Value.Trim();

            if (_webAgent != null)
            {
                AnsiConsole.MarkupLine($"[bold cyan]⚡ Spawning Web Agent:[/] [dim]{Markup.Escape(agentName)}[/]");
                
                try
                {
                    var webResult = await _webAgent.SendMessageAsync(agentName, taskInstruction, ct);
                    loopFeedBuilder.AppendLine($"Result of SPAWN '{agentName}':\n{webResult}\n");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    loopFeedBuilder.AppendLine($"Result of SPAWN '{agentName}': FAILED - {ex.Message}\n");
                    AnsiConsole.MarkupLine($"[bold red]✕ Web Agent Spawn Failed:[/] {Markup.Escape(ex.Message)}");
                }
                finally
                {
                    try { await _webAgent.CloseWorkerTabAsync(agentName); } catch { /* best-effort */ }
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold yellow]⚠ SPAWN requested but Web Agent unavailable. Falling back to LOCAL_WORKER...[/]");
                var fallbackResult = await SpawnLocalWorkerAsync(agentName, taskInstruction, ct);
                loopFeedBuilder.AppendLine($"Result of SPAWN->LOCAL_WORKER '{agentName}':\n{fallbackResult}\n");
            }
            actionExecuted = true;
        }

        // ═══════════════════════════════════════════════════════════
        // 7. SYNTAX ERROR DETECTION — catches malformed bracket tokens
        // ═══════════════════════════════════════════════════════════
        if (!actionExecuted)
        {
            // Detect malformed [FILE_WRITE:path]content (missing | separator)
            if (Regex.IsMatch(aiResponse, @"\[FILE_WRITE:\s*[^\|\]]+\]", RegexOptions.IgnoreCase))
            {
                AnsiConsole.MarkupLine("[bold red]⚠ SYNTAX ERROR detected:[/] [dim]FILE_WRITE missing '|' separator[/]");
                loopFeedBuilder.AppendLine("SYNTAX ERROR: You attempted [FILE_WRITE] but forgot the '|' separator. Correct format: [FILE_WRITE: filepath | content]. Try again with the correct syntax.");
                actionExecuted = true;
            }

            // Detect malformed [SPAWN_LOCAL_WORKER:persona] (missing | separator)
            if (Regex.IsMatch(aiResponse, @"\[SPAWN_LOCAL_WORKER:\s*[^\|\]]+\]", RegexOptions.IgnoreCase))
            {
                AnsiConsole.MarkupLine("[bold red]⚠ SYNTAX ERROR detected:[/] [dim]SPAWN_LOCAL_WORKER missing '|' separator[/]");
                loopFeedBuilder.AppendLine("SYNTAX ERROR: You attempted [SPAWN_LOCAL_WORKER] but forgot the '|' separator. Correct format: [SPAWN_LOCAL_WORKER: Persona | Task]. Try again.");
                actionExecuted = true;
            }

            // Detect malformed [SPAWN:name] (missing | separator)
            if (Regex.IsMatch(aiResponse, @"\[SPAWN:\s*[^\|\]]+\]", RegexOptions.IgnoreCase))
            {
                AnsiConsole.MarkupLine("[bold red]⚠ SYNTAX ERROR detected:[/] [dim]SPAWN missing '|' separator[/]");
                loopFeedBuilder.AppendLine("SYNTAX ERROR: You attempted [SPAWN] but forgot the '|' separator. Correct format: [SPAWN: AgentName | Task]. Try again.");
                actionExecuted = true;
            }
        }

        return new ToolExecutionResult(loopFeedBuilder.ToString(), actionExecuted, false);
    }

    /// <summary>
    /// Applies a <see cref="GuardResult"/>, prompting the human when the policy asks for it.
    /// Returns true when the caller may proceed; otherwise <paramref name="refusal"/>
    /// carries the message fed back to the AI so it can adapt instead of retrying blindly.
    /// </summary>
    private bool Authorize(ToolKind kind, string target, GuardResult verdict, out string refusal, string? preview = null)
    {
        refusal = string.Empty;

        switch (verdict.Decision)
        {
            case GuardDecision.Allow:
                return true;

            case GuardDecision.Deny:
                AnsiConsole.MarkupLine($"[bold red]🛡 BLOCKED[/] [dim]({Markup.Escape(verdict.Reason)})[/]");
                AnsiConsole.MarkupLine($"[dim red]  {Markup.Escape(Truncate(target, 200))}[/]");
                refusal = $"Blocked by the host safety policy: {verdict.Reason}. " +
                          "Do not retry this. Propose a different, safer approach.";
                return false;

            default:
                return PromptForApproval(kind, target, verdict, out refusal, preview);
        }
    }

    private bool PromptForApproval(ToolKind kind, string target, GuardResult verdict, out string refusal, string? preview)
    {
        refusal = string.Empty;

        var label = kind == ToolKind.Terminal ? "run a terminal command" : "write a file";
        var body = kind == ToolKind.Terminal
            ? target
            : $"{target}\n\n{Truncate(preview ?? string.Empty, 600)}";

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(new Markup($"[white]{Markup.Escape(Truncate(body, 1200))}[/]"))
        {
            Header = new PanelHeader($" 🛡  The AI wants to {label} ", Justify.Left),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 1, 1, 1)
        }.BorderColor(Color.Orange1));

        const string once = "✅ Allow once";
        const string always = "🔓 Allow, and don't ask again this session";
        const string skip = "⛔ Skip this action (the AI is told and can adapt)";
        const string abort = "🛑 Skip and cancel the whole task";

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[dim]{Markup.Escape(verdict.Reason)}[/] — how should we proceed?")
                .AddChoices(once, always, skip, abort));

        if (choice == once)
            return true;

        if (choice == always)
        {
            _guard.RememberApproval(kind, target);
            return true;
        }

        if (choice == abort)
        {
            AnsiConsole.MarkupLine("[bold red]🛑 Task cancelled by user at the approval prompt.[/]");
            throw new OperationCanceledException("Task cancelled by user at the approval prompt.");
        }

        AnsiConsole.MarkupLine("[yellow]⛔ Action skipped by user.[/]");
        refusal = "The human declined this action. Do not repeat it — choose a different approach " +
                  "or ask the human what they would prefer.";
        return false;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + $"… ({value.Length - max} more chars)";

    private async Task<string> PerformWebSearchAsync(string query, CancellationToken ct = default)
    {
        try 
        {
            var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
            var response = await _httpClient.GetAsync(url, ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            
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

    private async Task<string> SpawnLocalWorkerAsync(string persona, string task, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
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
