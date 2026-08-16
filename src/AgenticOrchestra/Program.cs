using System.Reflection;
using System.Text;
using Spectre.Console;
using AgenticOrchestra.Models;
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

        // ── Argument handling ───────────────────────────────────────
        // Informational flags answer and exit before any heavy startup work
        // (config write, Playwright download), so `orchestra --help` is instant.
        ApprovalMode? approvalOverride = null;
        bool? headlessOverride = null;

        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;

                case "-v":
                case "--version":
                    AnsiConsole.WriteLine(GetVersion());
                    return 0;

                case "--config-path":
                    AnsiConsole.WriteLine(ConfigService.ConfigFilePath);
                    return 0;

                case "--safe":
                    approvalOverride = ApprovalMode.ReadOnly;
                    break;

                case "--ask":
                    approvalOverride = ApprovalMode.Ask;
                    break;

                case "--auto":
                    approvalOverride = ApprovalMode.Auto;
                    break;

                case "--headless":
                    headlessOverride = true;
                    break;

                case "--headed":
                    headlessOverride = false;
                    break;

                default:
                    AnsiConsole.MarkupLine($"[red]Unknown option:[/] {Markup.Escape(arg)}");
                    AnsiConsole.WriteLine();
                    PrintUsage();
                    return 2;
            }
        }

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

            // Command-line overrides apply to this run only — never written back to disk.
            if (approvalOverride is { } mode)
            {
                config.Safety.ApprovalMode = mode;
                AnsiConsole.MarkupLine($"[dim]Safety override for this run:[/] {UIHelper.DescribeApprovalMode(mode)}");
            }

            if (headlessOverride is { } headless)
            {
                config.WebFallback.Headless = headless;
                AnsiConsole.MarkupLine($"[dim]Browser override for this run:[/] {(headless ? "headless" : "headed")}");
            }

            AnsiConsole.MarkupLine(
                $"[dim]Config:[/] [link={ConfigService.ConfigFilePath}]{ConfigService.ConfigFilePath}[/]");
            AnsiConsole.MarkupLine($"[dim]Safety:[/] {UIHelper.DescribeApprovalMode(config.Safety.ApprovalMode)}");
            AnsiConsole.WriteLine();

            // ── Layer 1 health check ────────────────────────────────
            // Surfaced at startup rather than on the first prompt: a running
            // Ollama with no models used to look fine and then fail mid-chat.
            var ollamaAgent = new OllamaAgent(config);
            var health = await AnsiConsole.Status()
                .SpinnerStyle(Style.Parse("magenta"))
                .StartAsync("[magenta]Checking local AI (Layer 1)...[/]", async _ => await ollamaAgent.CheckHealthAsync());

            if (isFirstRun && health.InstalledModels.Count > 0)
            {
                var selectedModel = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select your preferred default Ollama model:")
                        .AddChoices(health.InstalledModels));

                config.Ollama.Model = selectedModel;
                await configService.SaveAsync(config);
                AnsiConsole.MarkupLine($"[green]Default model set to {selectedModel}.[/]");
                health = await ollamaAgent.CheckHealthAsync();
            }

            if (health.IsUsable)
            {
                AnsiConsole.MarkupLine($"[dim]Layer 1:[/] [green]ready[/] [dim]({Markup.Escape(config.Ollama.Model)})[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]Layer 1:[/] [yellow]unavailable[/] [dim]— {Markup.Escape(health.Message)}[/]");
                AnsiConsole.MarkupLine("[dim]The app will run in hard-fallback mode, talking to the web AI directly.[/]");

                if (health.ServerUp && health.InstalledModels.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[dim]Installed models:[/] {Markup.Escape(string.Join(", ", health.InstalledModels))}");
                    AnsiConsole.MarkupLine("[dim]Pick one from Settings to enable the full pipeline.[/]");
                }
            }
            AnsiConsole.WriteLine();

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

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    private static void PrintUsage()
    {
        AnsiConsole.MarkupLine($"[bold cornflowerblue]Agentic Orchestra[/] [dim]v{GetVersion()}[/]");
        AnsiConsole.MarkupLine("[dim]A CLI orchestrator that drives local and web-based AIs as one team.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Usage:[/] orchestra [[options]]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[cyan]Option[/]");
        table.AddColumn("Description");
        table.AddRow("[bold]-h, --help[/]", "Show this help and exit.");
        table.AddRow("[bold]-v, --version[/]", "Print the version and exit.");
        table.AddRow("[bold]--config-path[/]", "Print the config.json location and exit.");
        table.AddRow("[bold]--ask[/]", "Confirm every command and file write (default).");
        table.AddRow("[bold]--auto[/]", "Run actions without asking. The blocklist still applies.");
        table.AddRow("[bold]--safe[/]", "Read-only: never execute commands or write files.");
        table.AddRow("[bold]--headed[/]", "Show the automated browser window.");
        table.AddRow("[bold]--headless[/]", "Hide the automated browser window.");
        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Safety and browser options override config.json for the current run only.[/]");
        AnsiConsole.MarkupLine("[dim]Run with no options for the interactive menu; use --help inside chat for session commands.[/]");
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
