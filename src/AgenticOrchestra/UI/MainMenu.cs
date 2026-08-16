using Spectre.Console;
using AgenticOrchestra.Models;
using AgenticOrchestra.Services;

namespace AgenticOrchestra.UI;

public static class MainMenu
{
    public static async Task RunAsync(AppConfig config, ConfigService configService)
    {
        var orchestrator = new OrchestratorService(config);

        while (true)
        {
            AnsiConsole.Clear();
            UIHelper.RenderBanner();
            
            AnsiConsole.Write(new Rule("[dim]Main Menu[/]").LeftJustified());
            AnsiConsole.MarkupLine(config.Ollama.Enabled
                ? $"Local AI: [green]{Markup.Escape(config.Ollama.Model)}[/]"
                : "Local AI: [cyan]off — web-only mode[/]");
            AnsiConsole.MarkupLine($"Web Manager: [fuchsia]{Markup.Escape(PlaywrightWebAgent.ResolveManagerPlatform(config).Name)}[/] [dim]{Markup.Escape(config.WebFallback.TargetUrl)}[/]");
            AnsiConsole.MarkupLine($"Squad: [cyan]Innovator({config.Squad.InnovatorPlatform})[/] + [cyan]Implementer({config.Squad.ImplementerPlatform})[/] → [yellow]Critic({config.Squad.CriticPlatform})[/]");
            AnsiConsole.MarkupLine($"Dream Threshold: [mediumpurple3]{config.Dreaming.TelemetryThreshold} telemetries[/]");
            AnsiConsole.MarkupLine($"Safety: {UIHelper.DescribeApprovalMode(config.Safety.ApprovalMode)}");
            AnsiConsole.WriteLine();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select an operation:")
                    .PageSize(10)
                    .AddChoices(new[]
                    {
                        "🤖 Start Chat",
                        "🔑 Login to AI Platforms",
                        "🧠 Dream Analysis",
                        "⚙️ Settings",
                        "📊 System Status",
                        "🚪 Exit"
                    }));

            switch (choice)
            {
                case "🤖 Start Chat":
                    await ChatView.RunAsync(orchestrator, config);
                    break;
                case "🔑 Login to AI Platforms":
                    await orchestrator.RunLoginFlowAsync();
                    AnsiConsole.MarkupLine("Press [green]Enter[/] to return to menu...");
                    Console.ReadLine();
                    break;
                case "🧠 Dream Analysis":
                    await RunDreamMenuAsync(orchestrator);
                    break;
                case "⚙️ Settings":
                    await ConfigMenu.RunAsync(config, configService);
                    orchestrator = new OrchestratorService(config);
                    break;
                case "📊 System Status":
                    await ShowStatusAsync(config, orchestrator);
                    break;
                case "🚪 Exit":
                    await orchestrator.RunExitDreamIfEnabledAsync();
                    AnsiConsole.MarkupLine("[yellow]Goodbye![/]");
                    return;
            }
        }
    }

    private static async Task RunDreamMenuAsync(OrchestratorService orchestrator)
    {
        AnsiConsole.Clear();
        UIHelper.RenderBanner();
        AnsiConsole.Write(new Rule("[dim]🧠 Dream Analysis — Sleep Mode Learning[/]").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]The Dreaming Service analyzes your past task telemetries to discover[/]");
        AnsiConsole.MarkupLine("[dim]patterns, recurring errors, and optimization opportunities.[/]");
        AnsiConsole.MarkupLine("[dim]Insights are injected into future sessions as learned knowledge.[/]");
        AnsiConsole.WriteLine();

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select dream action:")
                .AddChoices(new[]
                {
                    "💤 Run Dream Cycle Now",
                    "🔙 Back to Menu"
                }));

        if (action.Contains("Run Dream"))
        {
            await orchestrator.TriggerDreamAsync();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Press [green]Enter[/] to return to menu...");
            Console.ReadLine();
        }
    }

    private static async Task ShowStatusAsync(AppConfig config, OrchestratorService orchestrator)
    {
        AnsiConsole.Clear();
        UIHelper.RenderBanner();
        AnsiConsole.Write(new Rule("[dim]System Status[/]").LeftJustified());

        var health = config.Ollama.Enabled
            ? await AnsiConsole.Status()
                .StartAsync("Pinging Ollama...", async _ => await new OllamaAgent(config).CheckHealthAsync())
            : OllamaHealth.Disabled();

        var layer1Status = !config.Ollama.Enabled
            ? "[cyan]Off — web-only mode[/]"
            : health.IsUsable
                ? "[green]Online[/]"
                : $"[red]Unavailable[/] [dim]— {Markup.Escape(health.Message)}[/]";

        var managerPlatform = PlaywrightWebAgent.ResolveManagerPlatform(config);

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(4))
            .AddColumn()
            .AddRow("[b]Layer 1 (Local AI)[/]", layer1Status)
            .AddRow("[b]Ollama Target[/]", $"[link={config.Ollama.Endpoint}]{config.Ollama.Endpoint}[/]")
            .AddRow("[b]Ollama Model[/]", Markup.Escape(config.Ollama.Model))
            .AddRow("[b]Layer 2 (Web Manager)[/]", $"[fuchsia]{Markup.Escape(managerPlatform.Name)}[/] [dim]{Markup.Escape(managerPlatform.Url)}[/]")
            .AddRow("[b]Manager Tab[/]", orchestrator.IsHardFallback ? "[yellow]Fallback Active[/]" : "[dim]Standby[/]")
            .AddRow("[b]Dream Threshold[/]", $"{config.Dreaming.TelemetryThreshold} telemetries")
            .AddRow("[b]Auto Dream on Exit[/]", config.Dreaming.AutoDreamOnExit ? "[green]Enabled[/]" : "[red]Disabled[/]");

        var guard = new SafetyGuard(config.Safety);
        var safetyGrid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(4))
            .AddColumn()
            .AddRow("[b]Approval Mode[/]", UIHelper.DescribeApprovalMode(config.Safety.ApprovalMode))
            .AddRow("[b]Workspace Root[/]", Markup.Escape(guard.WorkspaceRoot))
            .AddRow("[b]Writes Confined[/]", config.Safety.ConfineFileWritesToWorkspace
                ? "[green]Yes — writes outside the workspace are refused[/]"
                : "[bold red]No — the AI may write anywhere on disk[/]")
            .AddRow("[b]Code Blocks Executed[/]", config.Safety.NormalizeCodeBlocks
                ? "[bold yellow]Yes — markdown blocks become commands[/]"
                : "[green]No[/]")
            .AddRow("[b]Blocked Patterns[/]", $"{config.Safety.BlockedCommandPatterns.Count} rules (enforced in every mode)");

        AnsiConsole.Write(new Panel(grid)
            {
                Header = new PanelHeader("Agentic Orchestra · 3-Layer Hierarchy"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1)
            });

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(safetyGrid)
            {
                Header = new PanelHeader("🛡  Safety"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1)
            }.BorderColor(config.Safety.ApprovalMode == ApprovalMode.Auto ? Color.Orange1 : Color.Grey));

        // Squad Configuration
        AnsiConsole.WriteLine();
        var squadGrid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(4))
            .AddColumn()
            .AddRow("[b]Innovator (Agent 2)[/]", config.Squad.InnovatorPlatform)
            .AddRow("[b]Implementer (Agent 3)[/]", config.Squad.ImplementerPlatform)
            .AddRow("[b]Critic (Agent 1)[/]", config.Squad.CriticPlatform)
            .AddRow("[b]Max Critic Retries[/]", config.Squad.MaxCriticRetries.ToString());

        AnsiConsole.Write(new Panel(squadGrid)
            {
                Header = new PanelHeader("Squad Configuration"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1)
            });

        // Platforms
        AnsiConsole.WriteLine();
        var platformTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        platformTable.AddColumn("[cyan]Platform[/]");
        platformTable.AddColumn("URL");
        platformTable.AddColumn("Status");

        foreach (var p in config.Platforms)
        {
            platformTable.AddRow(
                p.Name,
                p.Url,
                p.Enabled ? "[green]Enabled[/]" : "[red]Disabled[/]"
            );
        }

        AnsiConsole.Write(platformTable);

        // Timeout Configuration
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Timeouts: Input={config.Timeouts.InputDetectionSeconds}s | Response={config.Timeouts.ResponseGenerationSeconds}s | Stall={config.Timeouts.StallDetectionSeconds}s | Terminal={config.Timeouts.TerminalCommandSeconds}s[/]");

        AnsiConsole.WriteLine();
        if (health.InstalledModels.Count > 0)
        {
            AnsiConsole.MarkupLine("[b]Local Models Available:[/]");
            foreach (var m in health.InstalledModels)
            {
                var marker = OllamaAgent.ModelMatches(new[] { m }, config.Ollama.Model) ? "[green]*[/]" : " ";
                AnsiConsole.MarkupLine($" {marker} {Markup.Escape(m)}");
            }
        }
        else if (config.Ollama.Enabled && health.ServerUp)
        {
            AnsiConsole.MarkupLine("[yellow]No local models found on this instance.[/]");
            AnsiConsole.MarkupLine($"[dim]Install one with:[/] ollama pull {Markup.Escape(config.Ollama.Model)}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Press [green]Enter[/] to return to menu...");
        Console.ReadLine();
    }
}
