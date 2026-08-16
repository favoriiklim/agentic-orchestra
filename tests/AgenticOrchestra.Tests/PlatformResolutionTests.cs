using Xunit;
using AgenticOrchestra.Models;
using AgenticOrchestra.Services;

namespace AgenticOrchestra.Tests;

/// <summary>
/// Covers which web platform plays the Manager role, and the web-only switch
/// that makes the local model optional.
/// </summary>
public class PlatformResolutionTests
{
    private static AppConfig ConfigWith(params (string Name, string Url, bool Enabled)[] platforms)
    {
        var config = new AppConfig
        {
            Platforms = platforms
                .Select(p => new AiPlatformConfig { Name = p.Name, Url = p.Url, Enabled = p.Enabled })
                .ToList()
        };
        return config;
    }

    // ── Manager resolution ───────────────────────────────────────────

    [Fact]
    public void ManagerPlatform_IsResolvedByName()
    {
        var config = ConfigWith(
            ("Gemini", "https://gemini.google.com/app", true),
            ("Claude", "https://claude.ai/new", true));
        config.WebFallback.ManagerPlatform = "Claude";

        Assert.Equal("Claude", PlaywrightWebAgent.ResolveManagerPlatform(config).Name);
    }

    [Fact]
    public void ManagerResolution_IsCaseInsensitive()
    {
        var config = ConfigWith(("ChatGPT", "https://chatgpt.com", true));
        config.WebFallback.ManagerPlatform = "chatgpt";

        Assert.Equal("ChatGPT", PlaywrightWebAgent.ResolveManagerPlatform(config).Name);
    }

    [Fact]
    public void DisabledPlatform_IsNotChosenAsManager()
    {
        var config = ConfigWith(
            ("Gemini", "https://gemini.google.com/app", false),
            ("Claude", "https://claude.ai/new", true));
        config.WebFallback.ManagerPlatform = "Gemini";
        config.WebFallback.TargetUrl = "https://nowhere.example";

        // Named platform is disabled and the URL matches nothing, so it must fall
        // through to an enabled platform rather than returning a dead tab target.
        Assert.Equal("Claude", PlaywrightWebAgent.ResolveManagerPlatform(config).Name);
    }

    [Fact]
    public void LegacyConfig_WithoutManagerName_FallsBackToUrlMatch()
    {
        // Configs written before ManagerPlatform existed identified the manager
        // only by TargetUrl; those must keep working.
        var config = ConfigWith(
            ("Gemini", "https://gemini.google.com/app", true),
            ("Claude", "https://claude.ai/new", true));
        config.WebFallback.ManagerPlatform = "";
        config.WebFallback.TargetUrl = "https://claude.ai/new";

        Assert.Equal("Claude", PlaywrightWebAgent.ResolveManagerPlatform(config).Name);
    }

    [Fact]
    public void UnknownManagerName_FallsBackToFirstEnabled()
    {
        var config = ConfigWith(
            ("Gemini", "https://gemini.google.com/app", false),
            ("ChatGPT", "https://chatgpt.com", true));
        config.WebFallback.ManagerPlatform = "Mistral";
        config.WebFallback.TargetUrl = "https://nowhere.example";

        Assert.Equal("ChatGPT", PlaywrightWebAgent.ResolveManagerPlatform(config).Name);
    }

    [Fact]
    public void NoPlatformsAtAll_FallsBackToBuiltInDefault()
    {
        var config = ConfigWith();
        config.WebFallback.ManagerPlatform = "Whatever";

        // Never null: the orchestrator always needs somewhere to open a tab.
        Assert.NotNull(PlaywrightWebAgent.ResolveManagerPlatform(config));
    }

    // ── Defaults ─────────────────────────────────────────────────────

    [Fact]
    public void DefaultConfig_ResolvesToGemini()
    {
        Assert.Equal("Gemini", PlaywrightWebAgent.ResolveManagerPlatform(new AppConfig()).Name);
    }

    [Fact]
    public void LocalAi_IsEnabledByDefault()
    {
        Assert.True(new AppConfig().Ollama.Enabled);
    }

    [Fact]
    public void DisabledLocalAi_ReportsNotUsable_WithoutProbing()
    {
        var health = OllamaHealth.Disabled();

        Assert.False(health.IsUsable);
        Assert.False(health.ServerUp);
        Assert.Contains("disabled", health.Message, StringComparison.OrdinalIgnoreCase);
    }
}
