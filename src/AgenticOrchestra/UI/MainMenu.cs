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
            AnsiConsole.MarkupLine($"Current Default Model: [green]{config.Ollama.Model}[/]");
            AnsiConsole.MarkupLine($"Web Fallback URL: [blue]{config.WebFallback.TargetUrl}[/]");
            AnsiConsole.MarkupLine($"Squad: [cyan]Innovator({config.Squad.InnovatorPlatform})[/] + [cyan]Implementer({config.Squad.ImplementerPlatform})[/] → [yellow]Critic({config.Squad.CriticPlatform})[/]");
            AnsiConsole.MarkupLine($"Dream Threshold: [mediumpurple3]{config.Dreaming.TelemetryThreshold} telemetries[/]");
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

        var isLocalUp = await AnsiConsole.Status()
            .StartAsync("Pinging Ollama...", async _ =>
            {
                var agent = new OllamaAgent(config);
                return await agent.IsAvailableAsync();
            });

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(4))
            .AddColumn()
            .AddRow("[b]Layer 1 (Local AI)[/]", isLocalUp ? "[green]Online[/]" : "[red]Offline — Hard Fallback will activate[/]")
            .AddRow("[b]Ollama Target[/]", $"[link={config.Ollama.Endpoint}]{config.Ollama.Endpoint}[/]")
            .AddRow("[b]Ollama Model[/]", config.Ollama.Model)
            .AddRow("[b]Layer 2 (Web Manager)[/]", $"[link={config.WebFallback.TargetUrl}]{config.WebFallback.TargetUrl}[/]")
            .AddRow("[b]Manager Tab[/]", orchestrator.IsHardFallback ? "[yellow]Fallback Active[/]" : "[dim]Standby[/]")
            .AddRow("[b]Dream Threshold[/]", $"{config.Dreaming.TelemetryThreshold} telemetries")
            .AddRow("[b]Auto Dream on Exit[/]", config.Dreaming.AutoDreamOnExit ? "[green]Enabled[/]" : "[red]Disabled[/]");

        AnsiConsole.Write(new Panel(grid)
            {
                Header = new PanelHeader("Agentic Orchestra · 3-Layer Hierarchy"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1)
            });

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
        var models = await new OllamaAgent(config).GetModelsAsync();
        if (models.Any())
        {
            AnsiConsole.MarkupLine("[b]Local Models Available:[/]");
            foreach (var m in models)
            {
                var marker = m == config.Ollama.Model ? "[green]*[/]" : " ";
                AnsiConsole.MarkupLine($" {marker} {m}");
            }
        }
        else if (isLocalUp)
        {
            AnsiConsole.MarkupLine("[yellow]No local models found on this instance.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Press [green]Enter[/] to return to menu...");
        Console.ReadLine();
    }
}
