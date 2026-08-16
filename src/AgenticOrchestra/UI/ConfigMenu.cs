using Spectre.Console;
using AgenticOrchestra.Models;
using AgenticOrchestra.Services;

namespace AgenticOrchestra.UI;

/// <summary>
/// Sectioned settings menu.
///
/// Earlier this was a single forced walkthrough: to change one timeout you had to
/// answer every question in order. Now each area is its own screen and the user
/// edits only what they came for.
/// </summary>
public static class ConfigMenu
{
    public static async Task RunAsync(AppConfig config, ConfigService configService)
    {
        var dirty = false;

        while (true)
        {
            AnsiConsole.Clear();
            UIHelper.RenderBanner();
            AnsiConsole.Write(new Rule("[dim]⚙️  Settings[/]").LeftJustified());
            RenderSummary(config);

            const string local = "🧠 Local AI (Ollama)";
            const string platforms = "🌐 Web Platforms";
            const string manager = "👑 Web Manager (Layer 2)";
            const string squad = "👥 Squad Roles (Layer 3)";
            const string safety = "🛡  Safety";
            const string dreaming = "💤 Dreaming";
            const string timeouts = "⏱  Timeouts";
            const string prompt = "📝 System Prompt";
            const string back = "🔙 Save & Back";

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Which settings would you like to change?")
                    .PageSize(12)
                    .AddChoices(local, platforms, manager, squad, safety, dreaming, timeouts, prompt, back));

            if (choice == back) break;

            if (choice == local) dirty |= await EditLocalAiAsync(config);
            else if (choice == platforms) dirty |= EditPlatforms(config);
            else if (choice == manager) dirty |= EditManager(config);
            else if (choice == squad) dirty |= EditSquad(config);
            else if (choice == safety) dirty |= EditSafety(config);
            else if (choice == dreaming) dirty |= EditDreaming(config);
            else if (choice == timeouts) dirty |= EditTimeouts(config);
            else if (choice == prompt) dirty |= EditSystemPrompt(config);
        }

        if (dirty)
        {
            await configService.SaveAsync(config);
            AnsiConsole.MarkupLine("[green]✓ Settings saved.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]No changes made.[/]");
        }

        AnsiConsole.MarkupLine("Press [green]Enter[/] to return to menu...");
        Console.ReadLine();
    }

    /// <summary>Shows the current shape of the pipeline at a glance.</summary>
    private static void RenderSummary(AppConfig config)
    {
        var managerName = PlaywrightWebAgent.ResolveManagerPlatform(config).Name;
        var enabled = config.Platforms.Where(p => p.Enabled).Select(p => p.Name).ToList();

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(3))
            .AddColumn()
            .AddRow("[b]Local AI[/]", config.Ollama.Enabled
                ? $"[green]on[/] [dim]· {Markup.Escape(config.Ollama.Model)}[/]"
                : "[yellow]off[/] [dim]· web-only mode[/]")
            .AddRow("[b]Web Manager[/]", $"[fuchsia]{Markup.Escape(managerName)}[/]")
            .AddRow("[b]Enabled sites[/]", enabled.Count > 0
                ? Markup.Escape(string.Join(", ", enabled))
                : "[red]none — enable at least one[/]")
            .AddRow("[b]Squad[/]", $"{Markup.Escape(config.Squad.InnovatorPlatform)} + {Markup.Escape(config.Squad.ImplementerPlatform)} → {Markup.Escape(config.Squad.CriticPlatform)}")
            .AddRow("[b]Safety[/]", UIHelper.DescribeApprovalMode(config.Safety.ApprovalMode));

        AnsiConsole.Write(new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0, 1, 0)
        }.BorderColor(Color.Grey));
        AnsiConsole.WriteLine();
    }

    // ── Local AI ─────────────────────────────────────────────────────

    private static async Task<bool> EditLocalAiAsync(AppConfig config)
    {
        Section("🧠 Local AI (Ollama)");
        AnsiConsole.MarkupLine("[dim]Layer 1 classifies prompts and phrases the final answer.[/]");
        AnsiConsole.MarkupLine("[dim]Switch it off to run entirely on web AIs — no Ollama install needed.[/]");
        AnsiConsole.WriteLine();

        config.Ollama.Enabled = AnsiConsole.Confirm("Use a local AI (Ollama)?", config.Ollama.Enabled);

        if (!config.Ollama.Enabled)
        {
            AnsiConsole.MarkupLine("[cyan]Web-only mode: prompts go straight to the Web Manager.[/]");
            Pause();
            return true;
        }

        config.Ollama.Endpoint = Ask("Ollama endpoint URL", config.Ollama.Endpoint);

        var agent = new OllamaAgent(config);
        var health = await AnsiConsole.Status()
            .SpinnerStyle(Style.Parse("magenta"))
            .StartAsync("Contacting Ollama...", async _ => await agent.CheckHealthAsync());

        if (health.InstalledModels.Count > 0)
        {
            const string manual = "✏️  Enter a name manually";
            var choices = new List<string>(health.InstalledModels) { manual };

            var picked = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"Default model [dim](current: {Markup.Escape(config.Ollama.Model)})[/]:")
                    .PageSize(12)
                    .AddChoices(choices));

            config.Ollama.Model = picked == manual
                ? Ask("Model name", config.Ollama.Model)
                : picked;
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(health.Message)}[/]");
            AnsiConsole.MarkupLine("[dim]No models to choose from — enter a name and pull it with 'ollama pull <name>'.[/]");
            config.Ollama.Model = Ask("Model name", config.Ollama.Model);
        }

        config.Ollama.TimeoutSeconds = AskInt("Response timeout (seconds)", config.Ollama.TimeoutSeconds, 5, 3600);

        Pause();
        return true;
    }

    // ── Web platforms ────────────────────────────────────────────────

    private static bool EditPlatforms(AppConfig config)
    {
        var changed = false;

        while (true)
        {
            Section("🌐 Web Platforms");
            AnsiConsole.MarkupLine("[dim]Only enabled sites can host the Manager or a Squad role.[/]");
            AnsiConsole.MarkupLine("[dim]Log into them from the main menu → 'Login to AI Platforms'.[/]");
            AnsiConsole.WriteLine();

            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            table.AddColumn("[cyan]Platform[/]");
            table.AddColumn("Status");
            table.AddColumn("URL");
            foreach (var p in config.Platforms)
            {
                table.AddRow(
                    Markup.Escape(p.Name),
                    p.Enabled ? "[green]enabled[/]" : "[red]disabled[/]",
                    Markup.Escape(p.Url));
            }
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            const string back = "🔙 Back";
            var options = config.Platforms
                .Select(p => $"{(p.Enabled ? "✅" : "⬜")} {p.Name}")
                .Append(back)
                .ToList();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a platform to configure:")
                    .PageSize(10)
                    .AddChoices(options));

            if (choice == back) return changed;

            var name = choice[2..].Trim();
            var platform = config.Platforms.First(p => p.Name == name);

            Section($"🌐 {platform.Name}");

            platform.Enabled = AnsiConsole.Confirm($"Enable {platform.Name}?", platform.Enabled);
            platform.Url = Ask("Chat URL", platform.Url);
            platform.LoginUrl = Ask("Login URL", string.IsNullOrWhiteSpace(platform.LoginUrl) ? platform.Url : platform.LoginUrl);

            if (AnsiConsole.Confirm("Edit CSS selectors? [dim](only needed if the site's UI changed)[/]", false))
            {
                platform.InputSelectors = AskSelectorList("Input selectors", platform.InputSelectors);
                platform.ResponseSelectors = AskSelectorList("Response selectors", platform.ResponseSelectors);
            }

            // A disabled platform must not stay wired into a role.
            RepairRoleAssignments(config);
            changed = true;
        }
    }

    private static List<string> AskSelectorList(string label, List<string> current)
    {
        AnsiConsole.MarkupLine($"[dim]{label} — comma separated, tried in order. Leave empty to keep.[/]");
        AnsiConsole.MarkupLine($"[dim]Current: {Markup.Escape(string.Join(", ", current))}[/]");

        var input = AnsiConsole.Prompt(new TextPrompt<string>($"{label}:").AllowEmpty());
        if (string.IsNullOrWhiteSpace(input)) return current;

        return input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    // ── Manager & Squad ──────────────────────────────────────────────

    private static bool EditManager(AppConfig config)
    {
        Section("👑 Web Manager (Layer 2)");

        var enabled = EnabledPlatformNames(config);
        if (enabled.Count == 0)
        {
            NoPlatformsWarning();
            return false;
        }

        AnsiConsole.MarkupLine("[dim]The Manager is the persistent tab that plans and executes tasks.[/]");
        AnsiConsole.WriteLine();

        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"Manager platform [dim](current: {Markup.Escape(config.WebFallback.ManagerPlatform)})[/]:")
                .AddChoices(enabled));

        config.WebFallback.ManagerPlatform = picked;

        // Keep the legacy URL field consistent with the chosen platform.
        var platform = config.Platforms.First(p => p.Name == picked);
        config.WebFallback.TargetUrl = platform.Url;

        config.WebFallback.Headless = AnsiConsole.Confirm(
            "Hide the browser window (headless)?", config.WebFallback.Headless);
        config.WebFallback.EphemeralWebChat = AnsiConsole.Confirm(
            "Use temporary/ephemeral chats where supported?", config.WebFallback.EphemeralWebChat);

        Pause();
        return true;
    }

    private static bool EditSquad(AppConfig config)
    {
        Section("👥 Squad Roles (Layer 3)");

        var enabled = EnabledPlatformNames(config);
        if (enabled.Count == 0)
        {
            NoPlatformsWarning();
            return false;
        }

        AnsiConsole.MarkupLine("[dim]Innovator and Implementer run in parallel; the Critic reviews both.[/]");
        AnsiConsole.MarkupLine("[dim]Assigning different sites to each role gives genuinely different perspectives.[/]");
        AnsiConsole.WriteLine();

        config.Squad.InnovatorPlatform = PickRole("Innovator (ideas & architecture)", config.Squad.InnovatorPlatform, enabled);
        config.Squad.ImplementerPlatform = PickRole("Implementer (code & commands)", config.Squad.ImplementerPlatform, enabled);
        config.Squad.CriticPlatform = PickRole("Critic (quality gate)", config.Squad.CriticPlatform, enabled);
        config.Squad.MaxCriticRetries = AskInt("Max critic rework rounds", config.Squad.MaxCriticRetries, 0, 10);

        Pause();
        return true;
    }

    private static string PickRole(string role, string current, List<string> choices) =>
        AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"{role} [dim](current: {Markup.Escape(current)})[/]:")
                .AddChoices(choices));

    // ── Safety ───────────────────────────────────────────────────────

    private static bool EditSafety(AppConfig config)
    {
        Section("🛡  Safety");
        AnsiConsole.MarkupLine("[dim]The web AI's output is untrusted input. This is the boundary[/]");
        AnsiConsole.MarkupLine("[dim]between what it suggests and what runs on your machine.[/]");
        AnsiConsole.WriteLine();

        config.Safety.ApprovalMode = AnsiConsole.Prompt(
            new SelectionPrompt<ApprovalMode>()
                .Title("Approval mode for terminal commands and file writes:")
                .UseConverter(mode => mode switch
                {
                    ApprovalMode.Ask => "Ask      — confirm each action (recommended)",
                    ApprovalMode.Auto => "Auto     — run without asking (blocklist still applies)",
                    _ => "ReadOnly — never execute or write; inspection only"
                })
                .AddChoices(ApprovalMode.Ask, ApprovalMode.Auto, ApprovalMode.ReadOnly));

        config.Safety.ConfineFileWritesToWorkspace = AnsiConsole.Confirm(
            "Confine file writes to the workspace directory?",
            config.Safety.ConfineFileWritesToWorkspace);

        config.Safety.WorkspaceRoot = AnsiConsole.Prompt(
            new TextPrompt<string>("Workspace root [dim](empty = current directory)[/]:")
                .DefaultValue(config.Safety.WorkspaceRoot)
                .AllowEmpty());

        config.Safety.NormalizeCodeBlocks = AnsiConsole.Confirm(
            "Treat markdown code blocks in AI replies as executable commands? [dim](risky)[/]",
            config.Safety.NormalizeCodeBlocks);

        Pause();
        return true;
    }

    // ── Dreaming & timeouts ──────────────────────────────────────────

    private static bool EditDreaming(AppConfig config)
    {
        Section("💤 Dreaming");
        AnsiConsole.MarkupLine("[dim]Analyses past task telemetry for recurring errors and lessons.[/]");
        AnsiConsole.WriteLine();

        config.Dreaming.TelemetryThreshold = AskInt("Telemetries before an automatic dream", config.Dreaming.TelemetryThreshold, 1, 1000);
        config.Dreaming.AutoDreamEnabled = AnsiConsole.Confirm("Dream automatically once the threshold is hit?", config.Dreaming.AutoDreamEnabled);
        config.Dreaming.AutoDreamOnExit = AnsiConsole.Confirm("Dream on exit?", config.Dreaming.AutoDreamOnExit);

        Pause();
        return true;
    }

    private static bool EditTimeouts(AppConfig config)
    {
        Section("⏱  Timeouts");
        AnsiConsole.MarkupLine("[dim]Raise these on slow connections or when web AIs respond slowly.[/]");
        AnsiConsole.WriteLine();

        config.Timeouts.InputDetectionSeconds = AskInt("Input detection (s)", config.Timeouts.InputDetectionSeconds, 5, 600);
        config.Timeouts.ResponseGenerationSeconds = AskInt("Response generation (s)", config.Timeouts.ResponseGenerationSeconds, 10, 1800);
        config.Timeouts.StallDetectionSeconds = AskInt("Stall detection (s)", config.Timeouts.StallDetectionSeconds, 5, 600);
        config.Timeouts.TerminalCommandSeconds = AskInt("Terminal command (s)", config.Timeouts.TerminalCommandSeconds, 5, 3600);
        config.Timeouts.NavigationTimeoutMs = AskInt("Page navigation (ms)", config.Timeouts.NavigationTimeoutMs, 5000, 300000);

        Pause();
        return true;
    }

    // ── System prompt ────────────────────────────────────────────────

    private static bool EditSystemPrompt(AppConfig config)
    {
        Section("📝 System Prompt");
        AnsiConsole.MarkupLine("[dim]Leave empty to keep the current prompt.[/]");
        AnsiConsole.WriteLine();

        // Escaped: the prompt documents bracket tokens like [TERMINAL_EXEC: cmd]
        // that Spectre would otherwise parse as markup and throw on.
        var preview = config.SystemPrompt.Length > 400
            ? config.SystemPrompt[..400] + $"… (+{config.SystemPrompt.Length - 400} chars)"
            : config.SystemPrompt;
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(preview)}[/]");
        AnsiConsole.WriteLine();

        if (AnsiConsole.Confirm("Reset to the built-in default?", false))
        {
            config.SystemPrompt = new AppConfig().SystemPrompt;
            AnsiConsole.MarkupLine("[green]Reset to default.[/]");
            Pause();
            return true;
        }

        var replacement = AnsiConsole.Prompt(new TextPrompt<string>("New prompt:").AllowEmpty());
        if (string.IsNullOrWhiteSpace(replacement))
        {
            AnsiConsole.MarkupLine("[dim]Unchanged.[/]");
            Pause();
            return false;
        }

        config.SystemPrompt = replacement;
        Pause();
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static List<string> EnabledPlatformNames(AppConfig config) =>
        config.Platforms.Where(p => p.Enabled).Select(p => p.Name).ToList();

    private static void NoPlatformsWarning()
    {
        AnsiConsole.MarkupLine("[bold red]No platforms are enabled.[/]");
        AnsiConsole.MarkupLine("[dim]Enable at least one under 'Web Platforms' first.[/]");
        Pause();
    }

    /// <summary>
    /// Repoints the Manager and Squad roles at an enabled platform whenever the
    /// one they referenced has been switched off, so the config never names a
    /// disabled site.
    /// </summary>
    private static void RepairRoleAssignments(AppConfig config)
    {
        var enabled = EnabledPlatformNames(config);
        if (enabled.Count == 0) return;

        var fallback = enabled[0];

        bool IsLive(string name) => enabled.Any(e => e.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (!IsLive(config.WebFallback.ManagerPlatform))
        {
            config.WebFallback.ManagerPlatform = fallback;
            config.WebFallback.TargetUrl = config.Platforms.First(p => p.Name == fallback).Url;
            AnsiConsole.MarkupLine($"[yellow]Manager moved to {Markup.Escape(fallback)} (previous platform is disabled).[/]");
        }

        if (!IsLive(config.Squad.InnovatorPlatform)) config.Squad.InnovatorPlatform = fallback;
        if (!IsLive(config.Squad.ImplementerPlatform)) config.Squad.ImplementerPlatform = fallback;
        if (!IsLive(config.Squad.CriticPlatform)) config.Squad.CriticPlatform = fallback;
    }

    private static void Section(string title)
    {
        AnsiConsole.Clear();
        UIHelper.RenderBanner();
        AnsiConsole.Write(new Rule($"[dim]{title}[/]").LeftJustified());
        AnsiConsole.WriteLine();
    }

    private static string Ask(string label, string current) =>
        AnsiConsole.Prompt(new TextPrompt<string>($"{label}:").DefaultValue(current).AllowEmpty());

    private static int AskInt(string label, int current, int min, int max) =>
        AnsiConsole.Prompt(
            new TextPrompt<int>($"{label}:")
                .DefaultValue(current)
                .Validate(v => v >= min && v <= max
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"[red]Enter a value between {min} and {max}.[/]")));

    private static void Pause()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press [green]Enter[/] to continue...[/]");
        Console.ReadLine();
    }
}
