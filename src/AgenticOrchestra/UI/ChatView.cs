using Spectre.Console;
using AgenticOrchestra.Models;
using AgenticOrchestra.Services;

namespace AgenticOrchestra.UI;

/// <summary>
/// Renders the CLI interactive chat interface for the 3-Layer Hierarchical Chain with Squad Pattern.
/// Shows layer indicators, fallback warnings, and supports execution control commands.
/// Handles Ctrl+C gracefully via global CancellationTokenSource.
/// </summary>
public static class ChatView
{
    public static async Task RunAsync(OrchestratorService orchestrator, AppConfig config)
    {
        AnsiConsole.Clear();
        UIHelper.RenderBanner();
        AnsiConsole.Write(new Rule("[dim]3-Layer Autonomous Session · Local Model → Web Manager AI → Squad[/]").LeftJustified());
        AnsiConsole.MarkupLine("[dim]Use [bold cyan]--help[/] to view available CLI commands.[/]");
        AnsiConsole.WriteLine();

        // ── Ctrl+C Handler — graceful cancellation ──────────────────
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // Prevent process termination
            orchestrator.CancelCurrentTask();
        };

        while (true)
        {
            // Show fallback warning if active
            if (orchestrator.IsHardFallback)
            {
                AnsiConsole.MarkupLine("[bold yellow on black] ⚠️  HARD FALLBACK MODE — Ollama offline, direct to Web Manager AI [/]");
            }

            var prompt = AnsiConsole.Prompt(
                new TextPrompt<string>($"[bold green]You[/]>")
                    .PromptStyle("white")
            );

            if (string.IsNullOrWhiteSpace(prompt))
                continue;

            // ── Command Parser ──
            if (prompt.StartsWith("--"))
            {
                var cmd = prompt.Trim().ToLowerInvariant();

                if (cmd == "--exit")
                {
                    AnsiConsole.MarkupLine("[dim]Gracefully tearing down all layers...[/]");
                    await orchestrator.RunExitDreamIfEnabledAsync();
                    await orchestrator.DisposeAsync();
                    Environment.Exit(0);
                }

                if (cmd == "--back" || cmd == "--menu")
                {
                    AnsiConsole.MarkupLine("[dim italic]Returning to Main Menu. Web Manager AI context remains alive.[/]");
                    break;
                }

                if (cmd == "--clear")
                {
                    orchestrator.ClearHistory();
                    AnsiConsole.MarkupLine("[dim italic]Conversation history cleared.[/]");
                    AnsiConsole.WriteLine();
                    continue;
                }

                if (cmd == "--dream")
                {
                    AnsiConsole.MarkupLine("[mediumpurple3]💤 Manually triggering Dream Analysis...[/]");
                    await orchestrator.TriggerDreamAsync();
                    continue;
                }

                if (cmd == "--login")
                {
                    await orchestrator.RunLoginFlowAsync();
                    continue;
                }

                if (cmd == "--stop")
                {
                    orchestrator.CancelCurrentTask();
                    continue;
                }

                if (cmd == "--help")
                {
                    DrawHelpMenu(orchestrator, config);
                    continue;
                }

                AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(prompt)}'. Use [bold]--help[/] for a list of valid commands.[/]");
                continue;
            }

            // ── Core 3-Layer Pipeline Execution ──
            string response = await orchestrator.ProcessPromptAsync(prompt);

            var panel = new Panel(new Markup(Markup.Escape(response)))
            {
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1),
                Header = new PanelHeader($" {orchestrator.ActiveProviderName} ", Justify.Left)
            };

            // Color based on which layer handled the response
            if (orchestrator.IsHardFallback)
            {
                panel.BorderColor(Color.Yellow);
            }
            else if (orchestrator.IsLocalOnly)
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

    private static void DrawHelpMenu(OrchestratorService orchestrator, AppConfig config)
    {
        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[cyan]Command[/]");
        table.AddColumn("Description");

        table.AddRow("[bold]--help[/]", "Display this help menu and system hierarchy.");
        table.AddRow("[bold]--clear[/]", "Clear the active conversation history.");
        table.AddRow("[bold]--dream[/]", "Manually trigger Dream Analysis (sleep-mode learning).");
        table.AddRow("[bold]--login[/]", "Open browser visually to log into AI platforms (Gemini/ChatGPT/Claude).");
        table.AddRow("[bold]--stop[/]", "Cancel the currently running task gracefully.");
        table.AddRow("[bold]Ctrl+C[/]", "Same as --stop — interrupts the active task without crashing.");
        table.AddRow("[bold]--back[/] / [bold]--menu[/]", "Return to the main menu. Web Manager AI remains alive.");
        table.AddRow("[bold]--exit[/]", "Run exit dream (if enabled), teardown all layers, and exit.");

        AnsiConsole.Write(table);

        // 3-Layer Hierarchy + Squad Tree
        var tree = new Tree("[bold blue]3-Layer Hierarchy + Squad Pattern[/]");
        
        var layer1 = tree.AddNode(orchestrator.IsHardFallback 
            ? "[bold red]Layer 1: Local Model (OFFLINE)[/]" 
            : "[bold green]Layer 1: Local Model (Ollama/Gemma)[/]");
        layer1.AddNode("[dim]Role: Communication interface — classify & present[/]");

        var layer2 = tree.AddNode("[bold fuchsia]Layer 2: Web Manager AI (Persistent)[/]");
        layer2.AddNode("[dim]Role: Operational brain — plan, execute, orchestrate[/]");

        var layer3 = tree.AddNode("[bold yellow]Layer 3: Squad Agents (Ephemeral Triad)[/]");
        var squad = layer3.AddNode("[dim]Fixed Triad Pattern (parallel + review):[/]");
        squad.AddNode($"[cyan]Agent 2 (Innovator)[/] → {config.Squad.InnovatorPlatform}");
        squad.AddNode($"[cyan]Agent 3 (Implementer)[/] → {config.Squad.ImplementerPlatform}");
        squad.AddNode($"[yellow]Agent 1 (Critic)[/] → {config.Squad.CriticPlatform}");
        
        AnsiConsole.Write(tree);

        // Mode indicator
        AnsiConsole.WriteLine();
        if (orchestrator.IsHardFallback)
        {
            AnsiConsole.MarkupLine("[bold yellow]Current Mode: HARD FALLBACK (User ↔ Web Manager AI direct)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[bold green]Current Mode: NORMAL (User → Ollama → Web Manager AI → Ollama → User)[/]");
        }

        // Enabled platforms
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Enabled Platforms:[/]");
        foreach (var p in config.Platforms.Where(p => p.Enabled))
        {
            AnsiConsole.MarkupLine($"  [green]●[/] {p.Name} → {p.Url}");
        }
        foreach (var p in config.Platforms.Where(p => !p.Enabled))
        {
            AnsiConsole.MarkupLine($"  [red]○[/] {p.Name} (disabled)");
        }

        AnsiConsole.WriteLine();
    }
}
