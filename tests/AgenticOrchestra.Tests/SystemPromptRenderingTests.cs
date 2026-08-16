using Spectre.Console;
using Xunit;
using AgenticOrchestra.Models;

namespace AgenticOrchestra.Tests;

/// <summary>
/// Regression cover for the Settings menu crash.
///
/// The default system prompt documents bracket tools such as [TERMINAL_EXEC: cmd].
/// Spectre.Console reads square brackets as markup tags, so rendering the prompt
/// unescaped threw and took the Settings menu down the moment it opened.
/// </summary>
public class SystemPromptRenderingTests
{
    [Fact]
    public void DefaultSystemPrompt_ContainsBracketTokens()
    {
        // Guards the premise of the test below: if the prompt ever stops
        // containing brackets, the escaping requirement deserves a re-look.
        Assert.Contains("[TERMINAL_EXEC:", new AppConfig().SystemPrompt);
    }

    [Fact]
    public void DefaultSystemPrompt_RenderedRaw_Throws()
    {
        var prompt = new AppConfig().SystemPrompt;

        Assert.ThrowsAny<Exception>(() => new Markup(prompt));
    }

    [Fact]
    public void DefaultSystemPrompt_RenderedEscaped_DoesNotThrow()
    {
        var prompt = new AppConfig().SystemPrompt;

        var exception = Record.Exception(() => new Markup(Markup.Escape(prompt)));

        Assert.Null(exception);
    }
}
