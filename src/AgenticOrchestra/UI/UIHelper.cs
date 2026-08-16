using Spectre.Console;
using AgenticOrchestra.Models;

namespace AgenticOrchestra.UI;

/// <summary>
/// Provides centralized UI components for the AgenticOrchestra ecosystem.
/// Ensures consistent branding and layout across all system nodes.
/// </summary>
public static class UIHelper
{
    /// <summary>
    /// Renders the primary Figlet branding banner and system rule.
    /// This should be called immediately after any AnsiConsole.Clear() to maintain UI persistence.
    /// </summary>
    public static void RenderBanner()
    {
        AnsiConsole.Write(new FigletText("Agentic Orchestra")
            .LeftJustified()
            .Color(Color.CornflowerBlue));

        AnsiConsole.Write(new Rule("[dim]Hybrid AI Orchestrator — Local LLM · Web Fallback[/]")
            .RuleStyle(Style.Parse("grey"))
            .LeftJustified());
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Renders an approval mode as coloured markup, so the current level of
    /// protection is visible wherever the user is in the app.
    /// </summary>
    public static string DescribeApprovalMode(ApprovalMode mode) => mode switch
    {
        ApprovalMode.Ask => "[green]Ask[/] [dim]— confirm every command & file write[/]",
        ApprovalMode.Auto => "[bold yellow]Auto[/] [dim]— runs without asking (blocklist still applies)[/]",
        _ => "[cyan]ReadOnly[/] [dim]— inspection only, nothing is executed[/]"
    };
}
