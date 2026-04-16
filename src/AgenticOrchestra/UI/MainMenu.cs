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
            
            AnsiConsole.Write(new Rule("[dim]Main Menu[/]").LeftJustified());
            AnsiConsole.MarkupLine($"Current Default Model: [green]{config.Ollama.Model}[/]");
            AnsiConsole.MarkupLine($"Web Fallback URL: [blue]{config.WebFallback.TargetUrl}[/]");
            AnsiConsole.WriteLine();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select an operation:")
                    .PageSize(10)
                    .AddChoices(new[]
                    {
                        "🤖 Start Chat",
                        "⚙️ Settings",
                        "📊 System Status",
                        "🚪 Exit"
                    }));

            switch (choice)
            {
                case "🤖 Start Chat":
                    await ChatView.RunAsync(orchestrator, config);
                    break;
                case "⚙️ Settings":
                    await ConfigMenu.RunAsync(config, configService);
                    // Re-initialize orchestrator in case settings changed
                    orchestrator = new OrchestratorService(config);
                    break;
                case "📊 System Status":
                    await ShowStatusAsync(config, orchestrator);
                    break;
                case "🚪 Exit":
                    AnsiConsole.MarkupLine("[yellow]Goodbye![/]");
                    return;
            }
        }
    }

    private static async Task ShowStatusAsync(AppConfig config, OrchestratorService orchestrator)
    {
        AnsiConsole.Clear();
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
            .AddRow("[b]Ollama Target[/]", $"[link={config.Ollama.Endpoint}]{config.Ollama.Endpoint}[/]")
            .AddRow("[b]Ollama Status[/]", isLocalUp ? "[green]Online[/]" : "[red]Offline[/]")
            .AddRow("[b]Web Fallback[/]", $"[link={config.WebFallback.TargetUrl}]{config.WebFallback.TargetUrl}[/]")
            .AddRow("[b]Config File[/]", ConfigService.ConfigFilePath)
            .AddRow("[b]Browser Profile[/]", ConfigService.BrowserProfilePath);

        AnsiConsole.Write(new Panel(grid)
            {
                Header = new PanelHeader("Agentic Orchestra Config"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1)
            });

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
