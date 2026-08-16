using Spectre.Console;
using AgenticOrchestra.Models;
using AgenticOrchestra.Services;

namespace AgenticOrchestra.UI;

public static class ConfigMenu
{
    public static async Task RunAsync(AppConfig config, ConfigService configService)
    {
        AnsiConsole.Clear();
        UIHelper.RenderBanner();
        AnsiConsole.Write(new Rule("[dim]Settings[/]").LeftJustified());

        // 1. Ollama Endpoint
        config.Ollama.Endpoint = AnsiConsole.Prompt(
            new TextPrompt<string>("Ollama Endpoint URL:")
                .DefaultValue(config.Ollama.Endpoint)
                .AllowEmpty());

        // 2. Ollama Model (Dynamic fetch from endpoint if available)
        var agent = new OllamaAgent(config);
        var models = await agent.GetModelsAsync();

        if (models.Any())
        {
            if (!models.Contains(config.Ollama.Model))
            {
                models.Add(config.Ollama.Model); // Ensure current is in list
            }

            config.Ollama.Model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Default Ollama Model:")
                    .AddChoices(models)
            );
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Could not automatically fetch models from Ollama (is it running?).[/]");
            config.Ollama.Model = AnsiConsole.Prompt(
                new TextPrompt<string>("Default Ollama Model Name:")
                    .DefaultValue(config.Ollama.Model)
                    .AllowEmpty());
        }

        // 3. Web Fallback Target URL
        config.WebFallback.TargetUrl = AnsiConsole.Prompt(
            new TextPrompt<string>("Web Fallback URL:")
                .DefaultValue(config.WebFallback.TargetUrl)
                .AllowEmpty());

        // 4. Web Fallback Headless Mode
        config.WebFallback.Headless = AnsiConsole.Confirm("Run web fallback in Headless mode (hidden browser)?", config.WebFallback.Headless);

        // 5. Safety — the boundary between AI suggestions and the user's machine
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[dim]🛡  Safety[/]").LeftJustified());

        config.Safety.ApprovalMode = AnsiConsole.Prompt(
            new SelectionPrompt<ApprovalMode>()
                .Title("Approval mode for terminal commands and file writes:")
                .UseConverter(mode => mode switch
                {
                    ApprovalMode.Ask => "Ask    — confirm each action (recommended)",
                    ApprovalMode.Auto => "Auto   — run without asking (blocklist still applies)",
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

        // 6. System Prompt
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[dim]System Prompt[/]").LeftJustified());
        AnsiConsole.MarkupLine("[dim]Leave empty to keep the current prompt.[/]");

        // Escaped: the prompt contains bracket tokens like [TERMINAL_EXEC: cmd]
        // that Spectre would otherwise parse as markup and throw on.
        var preview = config.SystemPrompt.Length > 400
            ? config.SystemPrompt[..400] + $"… (+{config.SystemPrompt.Length - 400} chars)"
            : config.SystemPrompt;
        AnsiConsole.MarkupLine($"[dim]Current:\n{Markup.Escape(preview)}[/]");

        var newSystemPrompt = AnsiConsole.Prompt(new TextPrompt<string>(">").AllowEmpty());

        if (!string.IsNullOrWhiteSpace(newSystemPrompt))
        {
            config.SystemPrompt = newSystemPrompt;
        }

        // Save
        await configService.SaveAsync(config);
        
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Settings saved successfully![/]");
        AnsiConsole.MarkupLine("Press [green]Enter[/] to return to menu...");
        Console.ReadLine();
    }
}
