using Spectre.Console;
using AgenticOrchestra.Models;
using AgenticOrchestra.Services;

namespace AgenticOrchestra.UI;

/// <summary>
/// Renders the CLI interactive chat interface, intercepting system commands and formatting outputs.
/// </summary>
public static class ChatView
{
    public static async Task RunAsync(OrchestratorService orchestrator, AppConfig config)
    {
        AnsiConsole.Clear();
        UIHelper.RenderBanner();
        AnsiConsole.Write(new Rule("[dim]Autonomous Multi-Agent Session[/]").LeftJustified());
        AnsiConsole.MarkupLine("[dim]Use [bold cyan]--help[/] to view available CLI commands.[/]");
        AnsiConsole.WriteLine();

        while (true)
        {
            var prompt = AnsiConsole.Prompt(
                new TextPrompt<string>($"[bold green]You[/]>")
                    .PromptStyle("white")
            );

            if (string.IsNullOrWhiteSpace(prompt))
                continue;

            // ── Command Parser ──
            if (prompt.StartsWith("--"))
            {
                if (prompt.Trim().Equals("--exit", StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine("[dim]Gracefully safely tearing down the Head Manager and background workers...[/]");
                    await orchestrator.DisposeAsync();
                    Environment.Exit(0);
                }

                if (prompt.Trim().Equals("--back", StringComparison.OrdinalIgnoreCase) || prompt.Trim().Equals("--menu", StringComparison.OrdinalIgnoreCase))
                {
                    // Breaking returns to MainMenu.cs. The Playwright Orchestrator context remains alive in the background!
                    AnsiConsole.MarkupLine("[dim italic]Returning to Main Menu. Background orchestration contexts remain alive.[/]");
                    break; 
                }

                if (prompt.Trim().Equals("--clear", StringComparison.OrdinalIgnoreCase))
                {
                    orchestrator.ClearHistory();
                    AnsiConsole.MarkupLine("[dim italic]Conversation history cleared.[/]");
                    AnsiConsole.WriteLine();
                    continue;
                }

                if (prompt.Trim().Equals("--help", StringComparison.OrdinalIgnoreCase))
                {
                    DrawHelpMenu();
                    continue;
                }

                AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(prompt)}'. Use [bold]--help[/] for a list of valid commands.[/]");
                continue;
            }

            string response = string.Empty;
            
            // Core Fallback Pipeline Execution
            response = await orchestrator.ProcessPromptAsync(prompt);

            var panel = new Panel(new Markup(Markup.Escape(response)))
            {
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1),
                Header = new PanelHeader($" {orchestrator.ActiveProviderName} ", Justify.Left)
            };

            // Differentiate colors based on backend provider gracefully
            if (orchestrator.IsUsingLocalAgent)
            {
                panel.BorderColor(Color.Cyan1);
            }
            else
            {
                panel.BorderColor(Color.Fuchsia);
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }
    }

    private static void DrawHelpMenu()
    {
        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[cyan]Command[/]");
        table.AddColumn("Description");

        table.AddRow("[bold]--help[/]", "Display this help menu and check agent statuses.");
        table.AddRow("[bold]--clear[/]", "Clear the active conversation history (Ollama only).");
        table.AddRow("[bold]--back[/] / [bold]--menu[/]", "Return to the main menu. Background agents remain alive.");
        table.AddRow("[bold]--exit[/]", "Terminate all agent websessions, save state, and exit app.");

        AnsiConsole.Write(table);

        var tree = new Tree("[bold blue]Active Swarm Nodes[/]");
        tree.AddNode("[bold fuchsia]Head Manager[/] (Identity Injected)");
        tree.AddNode("[bold yellow]Sub-Agents[/]")
            .AddNode("Worker 1")
            .AddNode("Coder")
            .AddNode("Writer");
        
        AnsiConsole.Write(tree);
        AnsiConsole.WriteLine();
    }
}
