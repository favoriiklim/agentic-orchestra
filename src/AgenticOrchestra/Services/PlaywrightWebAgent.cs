using System.Collections.Concurrent;
using Microsoft.Playwright;
using AgenticOrchestra.Models;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// Stateful Web Agent capable of managing multiple Agent Tabs/Personas in a single browser context.
/// </summary>
public sealed class PlaywrightWebAgent : IAsyncDisposable
{
    private readonly AppConfig _config;
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    
    // Tracks specific tabs dedicated to specific Agent Roles
    private readonly ConcurrentDictionary<string, IPage> _tabs = new(StringComparer.OrdinalIgnoreCase);

    private static string DebugScreenshotDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgenticOrchestra", "debug");

    public PlaywrightWebAgent(AppConfig config)
    {
        _config = config;
    }

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
            // Channel = "msedge",
            Args = new[] { "--disable-blink-features=AutomationControlled" },
            IgnoreHTTPSErrors = true
        };

        var profilePath = ConfigService.BrowserProfilePath;
        Log($"Mounting persistent context at: {profilePath}");
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(profilePath, launchOptions);
    }

    /// <summary>
    /// Gets an existing page for the agent, or spawns a new one and navigates to the AI platform.
    /// </summary>
    private async Task<IPage> GetOrCreateTabAsync(string agentName)
    {
        if (_context == null) throw new InvalidOperationException("PlaywrightWebAgent is not initialized.");

        if (_tabs.TryGetValue(agentName, out var existingPage))
        {
            await existingPage.BringToFrontAsync();
            return existingPage;
        }

        Log($"Spawning new isolated tab for agent: [cyan]{Markup.Escape(agentName)}[/]");
        
        // Use the initial blank page if it's the very first tab, otherwise open a new one.
        var newPage = _tabs.IsEmpty ? (_context.Pages.FirstOrDefault() ?? await _context.NewPageAsync()) : await _context.NewPageAsync();

        Log($"Navigating [cyan]{Markup.Escape(agentName)}[/] to {_config.WebFallback.TargetUrl}...");
        await newPage.GotoAsync(_config.WebFallback.TargetUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60000
        });
        
        Log($"Navigation complete for [cyan]{Markup.Escape(agentName)}[/].");
        _tabs[agentName] = newPage;
        return newPage;
    }

    /// <summary>
    /// Sends a prompt to the specifically named agent tab and extracts the response.
    /// </summary>
    public async Task<string> SendMessageAsync(string agentName, string prompt)
    {
        try
        {
            var page = await GetOrCreateTabAsync(agentName);

            // ── STEP 1: Wait for input element to exist ──────────
            Log($"({Markup.Escape(agentName)}) Polling DOM for input element...");
            string? foundSelector = null;

            try
            {
                foundSelector = await page.EvaluateAsync<string?>(@"async () => {
                    const selectors = [
                        'rich-textarea .ql-editor',
                        'rich-textarea textarea',
                        'rich-textarea p[data-placeholder]',
                        'rich-textarea [contenteditable=""true""]',
                        'rich-textarea',
                        '[contenteditable=""true""][role=""textbox""]',
                        '.ql-editor[contenteditable=""true""]',
                        'textarea[placeholder]'
                    ];

                    for (let i = 0; i < 60; i++) {
                        for (const s of selectors) {
                            const el = document.querySelector(s);
                            if (el && (el.offsetWidth > 0 || el.offsetHeight > 0)) {
                                return s;
                            }
                        }
                        await new Promise(r => setTimeout(r, 500));
                    }
                    return null;
                }");
            }
            catch (Exception ex)
            {
                Log($"(WARN) Polling JS threw: {Markup.Escape(ex.Message)}");
            }

            if (string.IsNullOrEmpty(foundSelector))
            {
                await SaveDebugScreenshot(page, $"no_input_element_{agentName}");
                return "(Error) Input element not found. Set Headless=false to check manually.";
            }

            await Task.Delay(1500);

            // ── STEP 2: Focus the element ────────────────────────
            await page.EvaluateAsync(@"(selector) => {
                const el = document.querySelector(selector);
                if (el) { el.focus(); el.click(); }
            }", foundSelector);
            await Task.Delay(300);

            // ── STEP 3: Insert Text ──────────────────────────────
            Log($"({Markup.Escape(agentName)}) Injecting prompt...");
            await page.Keyboard.InsertTextAsync(prompt);
            await Task.Delay(500);

            // ── STEP 4: Submit the Message ───────────────────────
            Log($"({Markup.Escape(agentName)}) Pressing Enter to invoke...");
            await page.Keyboard.PressAsync("Enter");

            // Wait a moment for UI transition
            await Task.Delay(1500);

            // ── STEP 5: Wait for and Extract Response ────────────
            Log($"({Markup.Escape(agentName)}) Polling DOM for AI response generation...");

            string lastText = "";
            int unchangedCount = 0;
            string? finalResponse = null;

            // Poll for up to 60 seconds
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(1000);
                
                // Enhanced Generation Sensing: Check if the 'Stop' button is visible natively
                bool isActivelyGenerating = false;
                try
                {
                    isActivelyGenerating = await page.EvaluateAsync<bool>(@"() => {
                        const stopBtns = document.querySelectorAll('button[aria-label*=""Stop""], button[aria-label*=""Durdur""]');
                        for (const btn of stopBtns) {
                            // If any stop button has physical dimensions, we are generating
                            if (btn.offsetWidth > 0 || btn.offsetHeight > 0) return true;
                        }
                        return false;
                    }");
                }
                catch { /* Ignore transient errors in UI sensing */ }

                string currentText = "";
                try
                {
                    currentText = await page.EvaluateAsync<string>(@"() => {
                        var elements = document.querySelectorAll('message-content, .markdown, article, .response-container');
                        if (elements.length > 0) {
                            return elements[elements.length - 1].innerText;
                        }
                        var broad = document.querySelectorAll('[class*=""response""], [class*=""answer""], [class*=""message""]');
                        if (broad.length > 0) {
                            return broad[broad.length - 1].innerText;
                        }
                        return '';
                    }");
                }
                catch (Exception evalEx)
                {
                    Log($"(WARN) Polling logic transient error: {Markup.Escape(evalEx.Message)}");
                }

                if (!string.IsNullOrWhiteSpace(currentText))
                {
                    // It is stable if it hasn't changed AND the UI 'Stop' button is not visible
                    if (currentText == lastText && !isActivelyGenerating)
                    {
                        unchangedCount++;
                        if (unchangedCount >= 3)
                        {
                            finalResponse = currentText;
                            Log($"[green]({Markup.Escape(agentName)}) Response generation stabilized.[/]");
                            break;
                        }
                    }
                    else
                    {
                        unchangedCount = 0;
                        lastText = currentText;
                        Log($"[dim]({Markup.Escape(agentName)}) Typing... ({currentText.Length} chars)[/]");
                    }
                }
                // If it's still blank but 'Stop' is visible, just patient.
                else if (isActivelyGenerating)
                {
                    Log($"[dim]({Markup.Escape(agentName)}) Thinking (UI generating)...[/]");
                }
            }

            if (finalResponse == null)
            {
                Log($"[yellow]({Markup.Escape(agentName)}) Response timed out. Returning last known text.[/]");
                finalResponse = lastText;
            }

            if (string.IsNullOrWhiteSpace(finalResponse))
            {
                return $"(Error) Empty response received from {agentName}.";
            }

            return finalResponse;
        }
        catch (Exception ex)
        {
            Log($"[red]CRITICAL EXCEPTION on {Markup.Escape(agentName)}[/]: {Markup.Escape(ex.Message)}");
            return $"[WebAgent Error] {ex.Message}";
        }
    }

    /// <summary>
    /// Safely shuts down the browser context and Playwright instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_context != null)
        {
            await _context.CloseAsync();
            await _context.DisposeAsync();
        }
        _playwright?.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

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
