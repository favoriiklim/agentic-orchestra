using Spectre.Console;

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
}
