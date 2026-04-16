using System.Text;
using Spectre.Console;
using AgenticOrchestra.Services;
using AgenticOrchestra.UI;

namespace AgenticOrchestra;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Enable UTF-8 for rich Spectre.Console rendering (emojis, box-drawing, etc.)
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            // ── Banner ──────────────────────────────────────────────
            UIHelper.RenderBanner();

            // ── Load Configuration ──────────────────────────────────
            bool isFirstRun = !File.Exists(ConfigService.ConfigFilePath);
            
            var configService = new ConfigService();
            var config = await AnsiConsole.Status()
                .AutoRefresh(true)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("[cyan]Loading configuration...[/]", async ctx =>
                {
                    var cfg = await configService.LoadAsync();
                    ctx.Status("[cyan]Configuration loaded.[/]");
                    return cfg;
                });

            AnsiConsole.MarkupLine(
                $"[dim]Config:[/] [link={ConfigService.ConfigFilePath}]{ConfigService.ConfigFilePath}[/]");
            AnsiConsole.WriteLine();

            // ── First Run Setup ─────────────────────────────────────
            if (isFirstRun)
            {
                AnsiConsole.MarkupLine("[bold yellow]First Run Detected: Fetching local models...[/]");
                var agent = new OllamaAgent(config);
                var models = await agent.GetModelsAsync();

                if (models.Any())
                {
                    var selectedModel = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Select your preferred default Ollama model:")
                            .AddChoices(models));

                    config.Ollama.Model = selectedModel;
                    await configService.SaveAsync(config);
                    AnsiConsole.MarkupLine($"[green]Default model set to {selectedModel}.[/]");
                    AnsiConsole.WriteLine();
                }
            }

            // ── Ensure Playwright Browsers ──────────────────────────
            await EnsurePlaywrightBrowsersAsync();

            // ── Launch Main Menu ────────────────────────────────────
            await MainMenu.RunAsync(config, configService);

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
    }

    /// <summary>
    /// Checks if Playwright browser binaries are installed.
    /// If not, downloads them automatically with a progress spinner.
    /// </summary>
    private static async Task EnsurePlaywrightBrowsersAsync()
    {
        var browsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ms-playwright");

        var chromiumDir = Directory.Exists(browsersPath)
            ? Directory.GetDirectories(browsersPath, "chromium-*").FirstOrDefault()
            : null;

        if (chromiumDir is not null)
        {
            AnsiConsole.MarkupLine("[dim]Playwright browsers: [green]OK[/][/]");
            AnsiConsole.WriteLine();
            return;
        }

        await AnsiConsole.Status()
            .AutoRefresh(true)
            .SpinnerStyle(Style.Parse("yellow"))
            .StartAsync("[yellow]Downloading Playwright browsers (first run only, may take a minute)...[/]", async ctx =>
            {
                var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

                if (exitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Playwright browser installation failed with exit code {exitCode}. " +
                        "Try running 'dotnet tool install --global Microsoft.Playwright.CLI' and then 'playwright install chromium' manually.");
                }

                await Task.CompletedTask;
                ctx.Status("[green]Playwright browsers installed.[/]");
            });

        AnsiConsole.MarkupLine("[green]✓ Playwright browsers downloaded successfully.[/]");
        AnsiConsole.WriteLine();
    }
}
