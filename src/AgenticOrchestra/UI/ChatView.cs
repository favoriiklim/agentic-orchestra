using Spectre.Console;
using AgenticOrchestra.Models;
using AgenticOrchestra.Services;

namespace AgenticOrchestra.UI;

public static class ChatView
{
    public static async Task RunAsync(OrchestratorService orchestrator, AppConfig config)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[dim]Chat Session[/]").LeftJustified());
        AnsiConsole.MarkupLine("[dim]Commands: [bold]/exit[/] to return to menu, [bold]/clear[/] to reset history.[/]");
        AnsiConsole.WriteLine();

        while (true)
        {
            var prompt = AnsiConsole.Prompt(
                new TextPrompt<string>($"[bold green]You[/]>")
                    .PromptStyle("white")
            );

            if (string.IsNullOrWhiteSpace(prompt))
                continue;

            if (prompt.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (prompt.Trim().Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                orchestrator.ClearHistory();
                AnsiConsole.MarkupLine("[dim italic]Conversation history cleared.[/]");
                AnsiConsole.WriteLine();
                continue;
            }

            string response = string.Empty;
            
            await AnsiConsole.Status()
                .SpinnerStyle(Style.Parse("magenta"))
                .StartAsync($"Agent is thinking ([dim]{orchestrator.ActiveProviderName}[/])...", async ctx =>
                {
                    response = await orchestrator.ProcessPromptAsync(prompt);
                    // Update the status message in case it fell back during the call
                    ctx.Status($"Agent is thinking ([dim]{orchestrator.ActiveProviderName}[/])...");
                });

            var panel = new Panel(new Markup(Markup.Escape(response)))
            {
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1),
                Header = new PanelHeader($" {orchestrator.ActiveProviderName} ", Justify.Left)
            };

            // Differentiate colors based on provider
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
}
