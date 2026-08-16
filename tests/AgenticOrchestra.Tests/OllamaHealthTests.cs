using Xunit;
using AgenticOrchestra.Services;

namespace AgenticOrchestra.Tests;

/// <summary>
/// Regression cover for the "Ollama is up but has no usable model" crash.
///
/// The old availability check only pinged /api/version. Ollama answers that
/// happily while holding zero models, so the pipeline entered normal mode and
/// then died on a 404 from /api/chat, taking the whole app down.
/// </summary>
public class OllamaHealthTests
{
    // ── Health state ─────────────────────────────────────────────────

    [Fact]
    public void ServerUpWithNoModel_IsNotUsable()
    {
        var health = new OllamaHealth(ServerUp: true, ModelPresent: false, new List<string>(), "no models");

        Assert.False(health.IsUsable);
    }

    [Fact]
    public void ServerDown_IsNotUsable()
    {
        Assert.False(OllamaHealth.Down("connection refused").IsUsable);
    }

    [Fact]
    public void ServerUpWithModel_IsUsable()
    {
        var health = new OllamaHealth(true, true, new List<string> { "llama3.2:latest" }, "Ready.");

        Assert.True(health.IsUsable);
    }

    // ── Model name matching ──────────────────────────────────────────

    [Fact]
    public void BareModelName_MatchesLatestTag()
    {
        // Ollama reports "llama3.2:latest" but users configure "llama3.2".
        var installed = new[] { "llama3.2:latest" };

        Assert.True(OllamaAgent.ModelMatches(installed, "llama3.2"));
    }

    [Fact]
    public void ExplicitTag_MatchesExactly()
    {
        var installed = new[] { "qwen2.5-coder:7b", "llama3.2:latest" };

        Assert.True(OllamaAgent.ModelMatches(installed, "qwen2.5-coder:7b"));
    }

    [Fact]
    public void ModelMatching_IsCaseInsensitive()
    {
        Assert.True(OllamaAgent.ModelMatches(new[] { "Llama3.2:Latest" }, "llama3.2"));
    }

    [Fact]
    public void MissingModel_DoesNotMatch()
    {
        var installed = new[] { "mistral:latest" };

        Assert.False(OllamaAgent.ModelMatches(installed, "llama3.2"));
    }

    [Fact]
    public void DifferentTagOfSameModel_DoesNotMatch()
    {
        // "llama3.2:1b" installed does not satisfy a request for plain
        // "llama3.2", which Ollama resolves to :latest.
        Assert.False(OllamaAgent.ModelMatches(new[] { "llama3.2:1b" }, "llama3.2"));
    }

    [Fact]
    public void NoModelsInstalled_MatchesNothing()
    {
        Assert.False(OllamaAgent.ModelMatches(Array.Empty<string>(), "llama3.2"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankConfiguredModel_DoesNotMatch(string configured)
    {
        Assert.False(OllamaAgent.ModelMatches(new[] { "llama3.2:latest" }, configured));
    }

    [Fact]
    public void ConfiguredModel_IsTrimmedBeforeMatching()
    {
        Assert.True(OllamaAgent.ModelMatches(new[] { "llama3.2:latest" }, "  llama3.2  "));
    }
}
