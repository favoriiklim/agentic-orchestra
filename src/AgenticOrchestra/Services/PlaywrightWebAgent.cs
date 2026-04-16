using Microsoft.Playwright;
using AgenticOrchestra.Models;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// Fallback AI agent that uses browser automation to interact with a web-based AI platform.
/// Uses Playwright's persistent context to retain login sessions.
/// Features aggressive multi-strategy input injection with deep debug logging.
/// </summary>
public sealed class PlaywrightWebAgent
{
    private readonly AppConfig _config;

    /// <summary>
    /// Path where debug screenshots are saved on failure.
    /// Uses the cross-platform AppData folder.
    /// </summary>
    private static string DebugScreenshotDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgenticOrchestra", "debug");

    public PlaywrightWebAgent(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Launches the browser, navigates to the target URL, submits the prompt,
    /// and attempts to extract the response.
    /// </summary>
    public async Task<string> SendPromptAsync(string prompt)
    {
        Log("Initializing Playwright...");
        using var playwright = await Playwright.CreateAsync();

        var launchOptions = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = _config.WebFallback.Headless,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            Channel = "msedge",
            Args = new[] { "--disable-blink-features=AutomationControlled" },
            IgnoreHTTPSErrors = true
        };

        var profilePath = ConfigService.BrowserProfilePath;
        Log($"Browser profile: {profilePath}");
        Log($"Channel: msedge | Headless: {_config.WebFallback.Headless}");

        Log("Launching persistent browser context...");
        await using var context = await playwright.Chromium.LaunchPersistentContextAsync(profilePath, launchOptions);

        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

        try
        {
            // ── STEP 1: Navigate ─────────────────────────────────
            Log($"Navigating to {_config.WebFallback.TargetUrl}...");
            await page.GotoAsync(_config.WebFallback.TargetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded
            });
            Log("Navigation complete (DOMContentLoaded reached). Relying on DOM polling next.");

            // ── STEP 2: Wait for input element to exist ──────────
            Log("Polling DOM for input element...");
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

                    // Poll up to 30 seconds (60 iterations × 500ms)
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
                await SaveDebugScreenshot(page, "no_input_element");
                return "(Error) Input element not found after 30s polling. Debug screenshot saved. " +
                       "Set Headless=false in Settings and log in manually.";
            }

            Log($"Input element found via selector: [cyan]{Markup.Escape(foundSelector)}[/]");

            // Extra delay for framework event listener hydration
            await Task.Delay(1500);

            // ── STEP 3: Focus the element ────────────────────────
            Log("Focusing input element...");
            await page.EvaluateAsync(@"(selector) => {
                const el = document.querySelector(selector);
                if (el) { el.focus(); el.click(); }
            }", foundSelector);
            await Task.Delay(300);
            Log("Focus applied.");

            // ── STEP 4: Attempt input via 3 strategies ───────────
            bool inputSucceeded = false;

            // ──── Plan A: InsertTextAsync (real textInput event) ──
            Log("(Plan A) Attempting page.Keyboard.InsertTextAsync...");
            try
            {
                await page.Keyboard.InsertTextAsync(prompt);
                await Task.Delay(500);

                // Verify text actually appeared
                var hasText = await page.EvaluateAsync<bool>(@"(selector) => {
                    const el = document.querySelector(selector);
                    if (!el) return false;
                    const text = el.innerText || el.textContent || el.value || '';
                    return text.trim().length > 0;
                }", foundSelector);

                if (hasText)
                {
                    Log("(Plan A) [green]SUCCESS[/] — text confirmed in DOM.");
                    inputSucceeded = true;
                }
                else
                {
                    Log("(Plan A) [yellow]FAILED[/] — text not detected in DOM after InsertTextAsync.");
                }
            }
            catch (Exception ex)
            {
                Log($"(Plan A) [red]EXCEPTION[/]: {Markup.Escape(ex.Message)}");
            }

            // ──── Plan B: Clipboard Paste (Ctrl+V) ──────────────
            if (!inputSucceeded)
            {
                Log("(Plan B) Attempting clipboard paste (Ctrl+V)...");
                try
                {
                    // Clear any existing content first
                    await page.Keyboard.PressAsync("Control+A");
                    await Task.Delay(100);
                    await page.Keyboard.PressAsync("Backspace");
                    await Task.Delay(100);

                    // Write prompt to clipboard via JS
                    await page.EvaluateAsync(@"(text) => navigator.clipboard.writeText(text)", prompt);
                    await Task.Delay(200);

                    // Paste
                    await page.Keyboard.PressAsync("Control+V");
                    await Task.Delay(500);

                    // Verify
                    var hasText = await page.EvaluateAsync<bool>(@"(selector) => {
                        const el = document.querySelector(selector);
                        if (!el) return false;
                        const text = el.innerText || el.textContent || el.value || '';
                        return text.trim().length > 0;
                    }", foundSelector);

                    if (hasText)
                    {
                        Log("(Plan B) [green]SUCCESS[/] — text confirmed via clipboard paste.");
                        inputSucceeded = true;
                    }
                    else
                    {
                        Log("(Plan B) [yellow]FAILED[/] — clipboard paste did not register.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"(Plan B) [red]EXCEPTION[/]: {Markup.Escape(ex.Message)}");
                }
            }

            // ──── Plan C: Direct JS injection + synthetic events ─
            if (!inputSucceeded)
            {
                Log("(Plan C) Attempting direct JS injection with event dispatch...");
                try
                {
                    var jsResult = await page.EvaluateAsync<bool>(@"(args) => {
                        const selector = args.selector;
                        const promptText = args.prompt;
                        const el = document.querySelector(selector);
                        if (!el) return false;

                        el.focus();

                        // Set content based on element type
                        if (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') {
                            const setter = Object.getOwnPropertyDescriptor(
                                window.HTMLTextAreaElement.prototype, 'value')?.set
                                || Object.getOwnPropertyDescriptor(
                                    window.HTMLInputElement.prototype, 'value')?.set;
                            if (setter) setter.call(el, promptText);
                            else el.value = promptText;
                        } else {
                            // contenteditable element
                            el.innerHTML = '';
                            el.textContent = promptText;
                            if (!el.textContent) el.innerHTML = '<p>' + promptText + '</p>';
                        }

                        // Fire every event framework might listen to
                        ['input','change','keydown','keyup','keypress','compositionstart','compositionend'].forEach(evtName => {
                            el.dispatchEvent(new Event(evtName, { bubbles: true, cancelable: true }));
                        });
                        el.dispatchEvent(new InputEvent('input', {
                            bubbles: true, cancelable: true,
                            inputType: 'insertText', data: promptText
                        }));

                        return true;
                    }", new { selector = foundSelector, prompt = prompt });

                    if (jsResult)
                    {
                        Log("(Plan C) [green]JS injection executed.[/] Checking DOM...");
                        await Task.Delay(500);
                        inputSucceeded = true; // Best effort — JS ran without error
                    }
                    else
                    {
                        Log("(Plan C) [yellow]FAILED[/] — JS could not find element.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"(Plan C) [red]EXCEPTION[/]: {Markup.Escape(ex.Message)}");
                }
            }

            // ──── All plans exhausted ────────────────────────────
            if (!inputSucceeded)
            {
                await SaveDebugScreenshot(page, "all_input_plans_failed");
                return "(Error) All 3 input strategies failed. Debug screenshot saved. " +
                       "Check the screenshot at: " + DebugScreenshotDir;
            }

            // ── STEP 5: Click the Send button ────────────────────
            Log("Waiting for Send button to activate...");
            await Task.Delay(1000);

            var sendClicked = await page.EvaluateAsync<bool>(@"() => {
                // Priority-ordered button selectors (multi-language)
                const btnSelectors = [
                    'button.send-button',
                    'button[aria-label*=""Send""]',
                    'button[aria-label*=""send""]',
                    'button[aria-label*=""Gönder""]',
                    'button[aria-label*=""gönder""]',
                    'button[data-at=""send""]',
                    '.send-button-container button',
                    'button[mat-icon-button]'
                ];

                for (const s of btnSelectors) {
                    const btn = document.querySelector(s);
                    if (btn && !btn.disabled) {
                        btn.click();
                        return true;
                    }
                }

                // Broad fallback: any enabled button with SVG and send-like label
                for (const btn of document.querySelectorAll('button:not([disabled])')) {
                    const label = (btn.getAttribute('aria-label') || btn.textContent || '').toLowerCase();
                    if (label.includes('send') || label.includes('gönder') || label.includes('submit') || label.includes('gonder')) {
                        btn.click();
                        return true;
                    }
                }
                return false;
            }");

            if (sendClicked)
            {
                Log("[green]Send button clicked successfully.[/]");
            }
            else
            {
                Log("[yellow]Send button not found, trying Enter key as fallback...[/]");
                await page.Keyboard.PressAsync("Enter");
            }

            // ── STEP 6: Wait for response ────────────────────────
            Log("Waiting for AI response (3s delay + NetworkIdle)...");
            await Task.Delay(3000);

            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                    new PageWaitForLoadStateOptions { Timeout = 60000 });
            }
            catch (TimeoutException)
            {
                Log("[yellow]NetworkIdle timeout (60s). Proceeding with extraction anyway...[/]");
            }

            await Task.Delay(1500);
            Log("Response wait complete. Extracting...");

            // ── STEP 7: Extract the response ─────────────────────
            var result = await page.EvaluateAsync<string>(@"() => {
                var elements = document.querySelectorAll('message-content, .markdown, article, .response-container');
                if (elements.length > 0) {
                    return elements[elements.length - 1].innerText;
                }
                // Broader fallback: any element with model-response class patterns
                var broad = document.querySelectorAll('[class*=""response""], [class*=""answer""], [class*=""message""]');
                if (broad.length > 0) {
                    return broad[broad.length - 1].innerText;
                }
                return 'Could not automatically extract response. Check browser visually.';
            }");

            Log($"[green]Response extracted[/] ({result?.Length ?? 0} chars).");
            return result ?? "(Error) Empty response received from browser.";
        }
        catch (Exception ex)
        {
            Log($"[red]CRITICAL EXCEPTION[/]: {Markup.Escape(ex.Message)}");
            await SaveDebugScreenshot(page, "critical_exception");
            return $"[WebAgent Error] {ex.Message}\nDebug screenshot saved to: {DebugScreenshotDir}";
        }
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Saves a debug screenshot to the debug directory with a timestamp.
    /// </summary>
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

    /// <summary>
    /// Logs a debug message to the console via Spectre.Console markup.
    /// </summary>
    private static void Log(string message)
    {
        AnsiConsole.MarkupLine($"[dim]WebAgent>[/] {message}");
    }
}
