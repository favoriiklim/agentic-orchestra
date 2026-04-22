using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using AgenticOrchestra.Models;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// Stateful Web Agent with multi-platform support (Gemini, ChatGPT, Claude).
/// Manages multiple Agent Tabs/Personas across different AI platforms in a single browser context.
///
/// In the 3-layer hierarchy:
///   - The "Manager" tab is PERSISTENT (Layer 2) and cannot be closed.
///   - Worker/Squad tabs (Layer 3) are EPHEMERAL — spawned and closed by the Manager's commands.
///
/// Supports CancellationToken for graceful task interruption.
/// </summary>
public sealed class PlaywrightWebAgent : IAsyncDisposable
{
    private readonly AppConfig _config;
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    
    // Tracks specific tabs dedicated to specific Agent Roles
    private readonly ConcurrentDictionary<string, IPage> _tabs = new(StringComparer.OrdinalIgnoreCase);

    // Maps agent names to their assigned platform configs (for multi-provider routing)
    private readonly ConcurrentDictionary<string, AiPlatformConfig> _agentPlatforms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Manager tab key — this tab is immortal and cannot be closed via CloseWorkerTabAsync.</summary>
    private const string ManagerTabKey = "Manager";

    private static string DebugScreenshotDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgenticOrchestra", "debug");

    public PlaywrightWebAgent(AppConfig config)
    {
        _config = config;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  INITIALIZATION & LIFECYCLE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initializes the long-running Playwright browser context.
    /// MUST be called once before sending messages.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_playwright != null) return;

        Log("Initializing Playwright Engine...");
        _playwright = await Playwright.CreateAsync();

        var launchOptions = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = _config.WebFallback.Headless,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            Args = new[] { "--disable-blink-features=AutomationControlled" },
            IgnoreHTTPSErrors = true
        };

        var profilePath = ConfigService.BrowserProfilePath;
        Log($"Mounting persistent context at: {profilePath}");
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(profilePath, launchOptions);
    }

    /// <summary>
    /// Tears down and rebuilds the browser context with a new headless setting.
    /// Used by the --login flow to toggle visibility.
    /// </summary>
    public async Task ReinitializeAsync(bool headless)
    {
        // Close existing context
        if (_context != null)
        {
            await _context.CloseAsync();
            await _context.DisposeAsync();
        }
        _playwright?.Dispose();

        _tabs.Clear();
        _agentPlatforms.Clear();
        _playwright = null;
        _context = null;

        // Re-launch with new headless setting
        _config.WebFallback.Headless = headless;
        await InitializeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  TAB MANAGEMENT (Multi-Platform Aware)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Gets an existing page for the agent, or spawns a new one and navigates to the platform.
    /// Uses the default TargetUrl unless a specific platform is assigned via AssignPlatform.
    /// </summary>
    private async Task<IPage> GetOrCreateTabAsync(string agentName, CancellationToken ct = default)
    {
        if (_context == null) throw new InvalidOperationException("PlaywrightWebAgent is not initialized.");
        ct.ThrowIfCancellationRequested();

        if (_tabs.TryGetValue(agentName, out var existingPage) && !existingPage.IsClosed)
        {
            await existingPage.BringToFrontAsync();
            return existingPage;
        }

        // Determine target URL: platform-specific or default
        string targetUrl = _config.WebFallback.TargetUrl;
        if (_agentPlatforms.TryGetValue(agentName, out var platform))
        {
            targetUrl = platform.Url;
        }

        Log($"Spawning tab for [cyan]{Markup.Escape(agentName)}[/] → {targetUrl}");
        
        var newPage = _tabs.IsEmpty 
            ? (_context.Pages.FirstOrDefault() ?? await _context.NewPageAsync()) 
            : await _context.NewPageAsync();

        await newPage.GotoAsync(targetUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = _config.Timeouts.NavigationTimeoutMs
        });
        
        Log($"Navigation complete for [cyan]{Markup.Escape(agentName)}[/].");
        _tabs[agentName] = newPage;
        return newPage;
    }

    /// <summary>
    /// Assigns a specific AI platform to an agent. Must be called BEFORE GetOrCreateTabAsync.
    /// </summary>
    public void AssignPlatform(string agentName, string platformName)
    {
        var platform = _config.Platforms.FirstOrDefault(
            p => p.Name.Equals(platformName, StringComparison.OrdinalIgnoreCase) && p.Enabled);

        if (platform != null)
        {
            _agentPlatforms[agentName] = platform;
            Log($"Platform [cyan]{platform.Name}[/] assigned to [cyan]{Markup.Escape(agentName)}[/]");
        }
        else
        {
            Log($"[yellow]Platform '{Markup.Escape(platformName)}' not found or disabled. Using default.[/]");
        }
    }

    /// <summary>
    /// Gets the platform config for an agent, falls back to the first enabled platform (Gemini).
    /// </summary>
    private AiPlatformConfig GetPlatformForAgent(string agentName)
    {
        if (_agentPlatforms.TryGetValue(agentName, out var assigned))
            return assigned;

        // Default: find the platform matching TargetUrl, or first enabled
        return _config.Platforms.FirstOrDefault(p => p.Url == _config.WebFallback.TargetUrl && p.Enabled)
            ?? _config.Platforms.FirstOrDefault(p => p.Enabled)
            ?? AiPlatformConfig.Defaults()[0]; // Ultimate fallback: Gemini
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MESSAGE SENDING (Platform-Aware + CancellationToken + Self-Healing)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends a prompt to the specifically named agent tab and extracts the response.
    /// Includes Self-Healing Stall Recovery: if the first attempt stalls (input not found
    /// OR response timeout with empty text), it resets the tab state and retries exactly once
    /// with a [SYSTEM RECOVERY] prefix. Only returns an error if the retry also fails.
    /// </summary>
    public async Task<string> SendMessageAsync(string agentName, string prompt, CancellationToken ct = default)
    {
        const int maxRetries = 1;
        var platform = GetPlatformForAgent(agentName);
        string currentPrompt = prompt;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var (result, isStall) = await SendMessageCoreAsync(agentName, currentPrompt, platform, ct);

                if (!isStall)
                {
                    // Success (or non-stall error message) — return directly
                    return result;
                }

                // ── STALL DETECTED — attempt self-healing ───────────
                if (attempt < maxRetries)
                {
                    Log($"[bold yellow][[SELF-HEALING]][/] Stall detected on [cyan]{Markup.Escape(agentName)}[/] ({platform.Name}). Initiating reset (attempt {attempt + 1}/{maxRetries + 1})...");
                    await SaveDebugScreenshot(
                        _tabs.TryGetValue(agentName, out var stallPage) ? stallPage : null!,
                        $"stall_{agentName}_{platform.Name}");

                    // Reset the tab to a fresh chat state
                    await ResetTabStateAsync(agentName, platform, ct);

                    // Prepend recovery context note to the prompt
                    currentPrompt = "[SYSTEM RECOVERY: Previous UI state stalled. Resuming task.]\n\n" + prompt;
                    Log($"[yellow][[SELF-HEALING]][/] Tab reset complete. Retrying with recovery prompt...");
                    continue;
                }

                // Retry exhausted — return the stall error
                Log($"[red][[SELF-HEALING FAILED]][/] Tab {Markup.Escape(agentName)} still stalled after {maxRetries + 1} attempts.");
                return result;
            }
            catch (OperationCanceledException)
            {
                Log($"[yellow]({Markup.Escape(agentName)}) Task cancelled by user.[/]");
                throw;
            }
            catch (Exception ex)
            {
                Log($"[red]CRITICAL EXCEPTION on {Markup.Escape(agentName)}[/]: {Markup.Escape(ex.Message)}");

                // On critical exception during first attempt, try self-healing once
                if (attempt < maxRetries)
                {
                    Log($"[yellow][[SELF-HEALING]][/] Attempting recovery after exception...");
                    try
                    {
                        await ResetTabStateAsync(agentName, platform, ct);
                        currentPrompt = "[SYSTEM RECOVERY: Previous UI state crashed. Resuming task.]\n\n" + prompt;
                        continue;
                    }
                    catch
                    {
                        return $"[WebAgent Error] Self-healing failed: {ex.Message}";
                    }
                }

                return $"[WebAgent Error] {ex.Message}";
            }
        }

        return $"(Error) All attempts exhausted for {agentName} on {platform.Name}.";
    }

    /// <summary>
    /// Core send-and-extract logic. Returns a tuple:
    ///   result: The response text or error message.
    ///   isStall: True if the tab stalled (input not found, or response timeout with empty text).
    /// </summary>
    private async Task<(string result, bool isStall)> SendMessageCoreAsync(
        string agentName, string prompt, AiPlatformConfig platform, CancellationToken ct)
    {
        var page = await GetOrCreateTabAsync(agentName, ct);
        var watchdog = Stopwatch.StartNew();

        // Build JS selectors from platform config
        var inputSelectorsJs = BuildSelectorArrayJs(platform.InputSelectors);
        var responseSelectorsJs = BuildSelectorArrayJs(platform.ResponseSelectors);
        var stopButtonSelectorsJs = BuildSelectorArrayJs(platform.StopButtonSelectors);

        // ── STEP 1: Wait for input element ──────────────────────────
        Log($"({Markup.Escape(agentName)}) Polling DOM for input element ({platform.Name})...");
        string? foundSelector = null;

        while (watchdog.Elapsed.TotalSeconds < _config.Timeouts.InputDetectionSeconds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foundSelector = await page.EvaluateAsync<string?>($@"() => {{
                    const selectors = {inputSelectorsJs};
                    for (const s of selectors) {{
                        const el = document.querySelector(s);
                        if (el && (el.offsetWidth > 0 || el.offsetHeight > 0)) return s;
                    }}
                    return null;
                }}").WaitAsync(TimeSpan.FromSeconds(5), ct);
                
                if (!string.IsNullOrEmpty(foundSelector)) break;
            }
            catch (Exception ex)
            {
                Log($"(WARN) Polling input JS threw: {Markup.Escape(ex.Message)}");
            }

            if (watchdog.Elapsed.TotalSeconds > _config.Timeouts.StallDetectionSeconds)
            {
                Log($"[yellow][[STABILITY]][/] Input stall on {platform.Name}. Silent reload...");
                await page.ReloadAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
                watchdog.Restart();
                await Task.Delay(2000, ct);
                continue;
            }

            await Task.Delay(1000, ct);
        }

        if (string.IsNullOrEmpty(foundSelector))
        {
            // ★ STALL: Input element never appeared — signal for self-healing
            Log($"[yellow]({Markup.Escape(agentName)}) INPUT STALL on {platform.Name} — input element not found.[/]");
            return ($"(Stall) Input element not found on {platform.Name}.", isStall: true);
        }

        await page.BringToFrontAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
        await Task.Delay(500, ct);

        // ── STEP 2: Focus & Inject ──────────────────────────────────
        Log($"({Markup.Escape(agentName)}) Forcing evaluate focus...");
        await page.EvaluateAsync(@"(selector) => {
            const el = document.querySelector(selector);
            if (el) { el.focus(); el.click(); }
        }", foundSelector).WaitAsync(TimeSpan.FromSeconds(5), ct);
        
        Log($"({Markup.Escape(agentName)}) Injecting prompt on {platform.Name}...");
        
        // Use a generous timeout for injection depending on prompt length, but strictly enforce cancellation
        var injectionTimeout = TimeSpan.FromSeconds(Math.Max(30, prompt.Length / 100)); 
        await page.Keyboard.InsertTextAsync(prompt).WaitAsync(injectionTimeout, ct);
        
        await Task.Delay(500, ct);
        await page.Keyboard.PressAsync("Enter").WaitAsync(TimeSpan.FromSeconds(5), ct);

        // ── STEP 3: Poll for Response (Platform-Aware) ──────────────
        Log($"({Markup.Escape(agentName)}) Polling {platform.Name} for response...");
        
        string lastText = "";
        int unchangedCount = 0;
        string? finalResponse = null;
        watchdog.Restart();

        var maxTime = TimeSpan.FromSeconds(_config.Timeouts.ResponseGenerationSeconds);

        while (watchdog.Elapsed < maxTime)
        {
            ct.ThrowIfCancellationRequested();

            // Detect active generation via stop button
            bool isActivelyGenerating = false;
            try
            {
                isActivelyGenerating = await page.EvaluateAsync<bool>($@"() => {{
                    const selectors = {stopButtonSelectorsJs};
                    for (const s of selectors) {{
                        const btns = document.querySelectorAll(s);
                        for (const btn of btns) {{
                            if (btn.offsetWidth > 0 || btn.offsetHeight > 0) return true;
                        }}
                    }}
                    return false;
                }}").WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch { /* ignore transient */ }

            // Extract response text
            string currentText = "";
            try
            {
                currentText = await page.EvaluateAsync<string>($@"() => {{
                    const selectors = {responseSelectorsJs};
                    for (const s of selectors) {{
                        var elements = document.querySelectorAll(s);
                        if (elements.length > 0) return elements[elements.length - 1].innerText;
                    }}
                    // Broad fallback
                    var broad = document.querySelectorAll('[class*=""response""], [class*=""answer""], [class*=""message""]');
                    if (broad.length > 0) return broad[broad.length - 1].innerText;
                    return '';
                }}").WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch { /* ignore transient */ }

            if (!string.IsNullOrWhiteSpace(currentText))
            {
                if (currentText == lastText && !isActivelyGenerating)
                {
                    unchangedCount++;
                    if (unchangedCount >= 3)
                    {
                        finalResponse = currentText;
                        Log($"[green]({Markup.Escape(agentName)}) Response stabilized on {platform.Name}.[/]");
                        break;
                    }
                }
                else
                {
                    unchangedCount = 0;
                    lastText = currentText;
                    watchdog.Restart();
                    Log($"[dim]({Markup.Escape(agentName)}) Generating... ({currentText.Length} chars)[/]");
                }
            }

            if (watchdog.Elapsed.TotalSeconds > _config.Timeouts.StallDetectionSeconds)
            {
                Log($"[yellow][[STABILITY]][/] Generation stall on {platform.Name}. Silent reload...");
                try { await page.ReloadAsync().WaitAsync(TimeSpan.FromSeconds(10), ct); } catch { }
                watchdog.Restart();
                await Task.Delay(2000, ct);
                continue;
            }

            int delay = isActivelyGenerating && currentText == lastText ? 3000 : 1000;
            await Task.Delay(delay, ct);
        }

        // Strict fallback to break hangs: we exceeded the maximum allowed generation time.
        if (watchdog.Elapsed >= maxTime)
        {
            Log($"[red]({Markup.Escape(agentName)}) HARD TIMEOUT reached for response generation ({_config.Timeouts.ResponseGenerationSeconds}s). Breaking loop.[/]");
        }

        if (finalResponse == null)
        {
            Log($"[yellow]({Markup.Escape(agentName)}) Checking last known text after exit.[/]");
            finalResponse = lastText;
        }

        if (string.IsNullOrWhiteSpace(finalResponse))
        {
            // ★ STALL: Response generation timed out with zero captured text
            Log($"[yellow]({Markup.Escape(agentName)}) RESPONSE STALL on {platform.Name} — no text captured.[/]");
            return ($"(Stall) No response captured from {agentName} on {platform.Name}.", isStall: true);
        }

        // ── SUCCESS: Got a valid response ───────────────────────────
        return (finalResponse, isStall: false);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SELF-HEALING: Stall Recovery
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resets a stalled tab to a fresh chat state.
    /// Strategy:
    ///   1. Try to click a platform-specific "New Chat" button.
    ///   2. If that fails, perform a hard navigation to the platform URL to rebuild the UI.
    /// The old tab page is replaced in the _tabs dictionary.
    /// </summary>
    private async Task ResetTabStateAsync(string agentName, AiPlatformConfig platform, CancellationToken ct)
    {
        string targetUrl = platform.Url;

        if (_tabs.TryGetValue(agentName, out var page) && !page.IsClosed)
        {
            // Strategy 1: Try to click a "New Chat" button
            bool newChatClicked = await TryClickNewChatAsync(page, platform);

            if (newChatClicked)
            {
                Log($"[green][[SELF-HEALING]][/] 'New Chat' clicked on {platform.Name}. Waiting for fresh state...");
                await Task.Delay(2000, ct);
                return;
            }
            
            // Strategy 2: Hard navigation — force the UI to rebuild from scratch
            Log($"[yellow][[SELF-HEALING]][/] Forcing hard navigation to {targetUrl}...");
            try
            {
                await page.GotoAsync(targetUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _config.Timeouts.NavigationTimeoutMs
                });
                await Task.Delay(2000, ct);
                Log($"[green][[SELF-HEALING]][/] Hard navigation complete for {Markup.Escape(agentName)}.");
                return;
            }
            catch (Exception ex)
            {
                Log($"[yellow][[SELF-HEALING]][/] Hard navigation failed: {Markup.Escape(ex.Message)}. Spawning new tab...");
                // Fall through to full tab replacement
            }
        }

        // Strategy 3: Nuclear option — close the old tab, open a fresh one
        if (_tabs.TryRemove(agentName, out var oldPage))
        {
            try { await oldPage.CloseAsync(); } catch { /* ignore */ }
        }

        if (_context == null) throw new InvalidOperationException("Browser context not initialized.");

        var newPage = await _context.NewPageAsync();
        await newPage.GotoAsync(targetUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = _config.Timeouts.NavigationTimeoutMs
        });
        _tabs[agentName] = newPage;
        await Task.Delay(2000, ct);

        Log($"[green][[SELF-HEALING]][/] Fresh tab spawned for {Markup.Escape(agentName)} on {platform.Name}.");
    }

    /// <summary>
    /// Attempts to click a platform-specific "New Chat" button to reset the conversation.
    /// Returns true if successful, false if no button was found or click failed.
    /// </summary>
    private static async Task<bool> TryClickNewChatAsync(IPage page, AiPlatformConfig platform)
    {
        // Platform-specific "New Chat" selectors 
        var newChatSelectors = platform.Name.ToLowerInvariant() switch
        {
            "gemini" => new[]
            {
                "a[href='/app']",                           // Gemini sidebar "New chat" link
                "button[aria-label*='New chat']",
                "button[aria-label*='Yeni sohbet']",        // Turkish locale
                "a.new-chat-button"
            },
            "chatgpt" => new[]
            {
                "a[href='/']",                              // ChatGPT "New chat" in sidebar
                "button[data-testid='create-new-chat-button']",
                "nav a:first-child"                         // First link in sidebar (usually New Chat)
            },
            "claude" => new[]
            {
                "a[href='/new']",                           // Claude "New chat" link
                "button[aria-label*='New chat']",
                "button[aria-label*='Start new']"
            },
            _ => new[] { "button[aria-label*='New chat']", "a[href*='new']" }
        };

        foreach (var selector in newChatSelectors)
        {
            try
            {
                var isVisible = await page.EvaluateAsync<bool>($@"() => {{
                    const el = document.querySelector('{selector.Replace("'", "\\'")}');
                    return el && (el.offsetWidth > 0 || el.offsetHeight > 0);
                }}");

                if (isVisible)
                {
                    await page.ClickAsync(selector, new PageClickOptions { Timeout = 5000 });
                    return true;
                }
            }
            catch
            {
                // Selector didn't match or click failed — try next
            }
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  LOGIN FLOW (--login command)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens visual (non-headless) browser tabs for each enabled AI platform.
    /// Returns the list of platforms that were opened for user login.
    /// </summary>
    public async Task<List<string>> OpenLoginTabsAsync()
    {
        if (_context == null) throw new InvalidOperationException("Browser not initialized.");

        var openedPlatforms = new List<string>();
        foreach (var platform in _config.Platforms.Where(p => p.Enabled))
        {
            var loginTabName = $"Login_{platform.Name}";
            var page = await _context.NewPageAsync();
            
            Log($"Opening login tab for [cyan]{platform.Name}[/] → {platform.LoginUrl}");
            await page.GotoAsync(platform.LoginUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _config.Timeouts.NavigationTimeoutMs
            });

            _tabs[loginTabName] = page;
            openedPlatforms.Add(platform.Name);
        }

        return openedPlatforms;
    }

    /// <summary>
    /// Closes all login tabs after the user has authenticated.
    /// </summary>
    public async Task CloseLoginTabsAsync()
    {
        var loginTabKeys = _tabs.Keys.Where(k => k.StartsWith("Login_")).ToList();
        foreach (var key in loginTabKeys)
        {
            if (_tabs.TryRemove(key, out var page))
            {
                try { await page.CloseAsync(); }
                catch { /* tab may already be closed */ }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  WORKER TAB LIFECYCLE (Layer 3 Management)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Closes a specific worker tab after its results have been collected by the Manager.
    /// The Manager tab ("Manager") is PROTECTED and cannot be closed via this method.
    /// </summary>
    public async Task CloseWorkerTabAsync(string agentName)
    {
        if (agentName.Equals(ManagerTabKey, StringComparison.OrdinalIgnoreCase))
        {
            Log($"[red]BLOCKED:[/] Attempted to close the immortal Manager tab. Operation denied.");
            return;
        }

        if (_tabs.TryRemove(agentName, out var page))
        {
            _agentPlatforms.TryRemove(agentName, out _);
            try
            {
                await page.CloseAsync();
                Log($"[yellow]Worker tab closed:[/] {Markup.Escape(agentName)}");
            }
            catch (Exception ex)
            {
                Log($"[red]Failed to close worker tab {Markup.Escape(agentName)}:[/] {Markup.Escape(ex.Message)}");
            }
        }
    }

    /// <summary>Checks whether a specific agent tab is still open and alive.</summary>
    public bool IsTabAlive(string agentName)
    {
        return _tabs.TryGetValue(agentName, out var page) && !page.IsClosed;
    }

    /// <summary>Returns the names of all currently active worker tabs (excludes Manager).</summary>
    public List<string> GetActiveWorkerNames()
    {
        return _tabs.Keys
            .Where(k => !k.Equals(ManagerTabKey, StringComparison.OrdinalIgnoreCase)
                     && !k.StartsWith("Login_"))
            .ToList();
    }

    /// <summary>Closes ALL worker tabs but keeps the Manager tab alive.</summary>
    public async Task CloseAllWorkersAsync()
    {
        var workerNames = GetActiveWorkerNames();
        foreach (var name in workerNames)
        {
            await CloseWorkerTabAsync(name);
        }
    }

    /// <summary>Safely shuts down the browser context and Playwright instance.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_context != null)
        {
            await _context.CloseAsync();
            await _context.DisposeAsync();
        }
        _playwright?.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Builds a JS array literal from a C# list of selector strings.</summary>
    private static string BuildSelectorArrayJs(List<string> selectors)
    {
        if (selectors == null || selectors.Count == 0)
            return "[]";

        var escaped = selectors.Select(s => $"'{s.Replace("'", "\\'")}'");
        return $"[{string.Join(", ", escaped)}]";
    }

    private static async Task SaveDebugScreenshot(IPage page, string label)
    {
        try
        {
            Directory.CreateDirectory(DebugScreenshotDir);
            var filename = $"{label}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var fullPath = Path.Combine(DebugScreenshotDir, filename);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = fullPath, FullPage = true });
            Log($"[yellow]Debug screenshot saved:[/] {Markup.Escape(fullPath)}");
        }
        catch (Exception ex)
        {
            Log($"[red]Failed to save screenshot:[/] {Markup.Escape(ex.Message)}");
        }
    }

    private static void Log(string message)
    {
        AnsiConsole.MarkupLine($"[dim]WebAgent>[/] {message}");
    }
}
