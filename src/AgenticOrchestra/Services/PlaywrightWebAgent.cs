using Microsoft.Playwright;
using AgenticOrchestra.Models;

namespace AgenticOrchestra.Services;

/// <summary>
/// Fallback AI agent that uses browser automation to interact with a web-based AI platform.
/// Uses Playwright's persistent context to retain login sessions.
/// </summary>
public sealed class PlaywrightWebAgent
{
    private readonly AppConfig _config;

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
        // 1. Initialize Playwright
        using var playwright = await Playwright.CreateAsync();

        // 2. Setup launch options
        var launchOptions = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = _config.WebFallback.Headless,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
        };

        // Determine executable path if we want to force Edge or Chrome, 
        // but by default Playwright resolves the bundled chromium.
        // We will just use the bundled chromium.
        var profilePath = ConfigService.BrowserProfilePath;

        // 3. Launch Persistent Context
        await using var context = await playwright.Chromium.LaunchPersistentContextAsync(profilePath, launchOptions);
        
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

        try
        {
            // 4. Navigate to target URL
            await page.GotoAsync(_config.WebFallback.TargetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            });

            // 5. Locate input field using accessibility locators
            var inputLocator = page.GetByPlaceholder(_config.WebFallback.InputPlaceholder);
            
            // Wait for it to be visible. If it fails, the user might need to log in.
            try
            {
                await inputLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            }
            catch (TimeoutException)
            {
                return "[Error] Input field not found. You may need to log into the AI platform first. Please ensure Headless is set to false in config and log in manually.";
            }

            // 6. Submit the prompt
            await inputLocator.FillAsync(prompt);
            await inputLocator.PressAsync("Enter");

            // 7. Wait for response stabilization
            // This is the trickiest part of web AI automation.
            // We wait for DOM mutations to settle and network to calm down.
            // A common approach for Gemini/ChatGPT is watching for the 'stop generating' / network idle.
            // For MVP, we wait a deterministic amount of time, then for network idle.
            await Task.Delay(3000); // Give the UI time to transition state
            
            // Wait until no active network requests for 1 second.
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 60000 });
            
            // Allow a tiny bit of extra time for final text rendering animations
            await Task.Delay(1000);

            // 8. Extract the response
            // To rely strictly on accessibility or semantic tags:
            // Most AI chats use `<article>`, `<message-content>` or ARIA live regions.
            // Gemini uses specifically styled elements. A generic fallback might be required.
            // As an MVP heuristic, we look for the last generated text block or just extract all text
            // from the main feed and get the last chunk. Since user said: "strict accessibility locators", 
            // Gemini responses are often marked with specific roles.
            
            // For Gemini MVP, all conversational text is generally in the body, but it's nested.
            // A very broad fallback is attempting to grab the inner text of the last conversational element.
            // If we can't find it easily via simple locators, we grab the page text, but for now let's use 
            // a general locator that applies to most chats. We fall back to standard evaluate if needed.
            
            var result = await page.EvaluateAsync<string>(@"() => {
                // Heuristic: Chat platforms often use 'user-content', 'message', or specific tags.
                // In Gemini, it's often within <message-content> elements.
                var elements = document.querySelectorAll('message-content, .markdown, article');
                if (elements.length > 0) {
                    return elements[elements.length - 1].innerText;
                }
                return 'Could not automatically extract response format. Check browser visually.';
            }");

            return result ?? "[Error] Empty response received from browser.";
        }
        catch (Exception ex)
        {
            return $"[WebAgent Error] {ex.Message}";
        }
        finally
        {
            // The context is closed correctly due to 'await using'
        }
    }
}
