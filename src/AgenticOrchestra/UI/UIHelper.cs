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
    /// True when there is a real terminal to prompt against.
    ///
    /// Spectre throws on any prompt when stdin is redirected, so callers must
    /// check this before asking a question the app could answer for itself
    /// (piped input, CI, `orchestra &lt; /dev/null`).
    /// </summary>
    public static bool IsInteractive => !Console.IsInputRedirected;

    /// <summary>
    /// Asks a yes/no question, or silently returns <paramref name="whenNonInteractive"/>
    /// when there is no terminal to ask.
    /// </summary>
    public static bool ConfirmIfInteractive(string question, bool defaultValue, bool whenNonInteractive)
    {
        if (!IsInteractive) return whenNonInteractive;
        return AnsiConsole.Confirm(question, defaultValue);
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
