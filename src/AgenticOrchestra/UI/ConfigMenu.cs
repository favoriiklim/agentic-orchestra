using Spectre.Console;
using AgenticOrchestra.Models;
using AgenticOrchestra.Services;

namespace AgenticOrchestra.UI;

public static class ConfigMenu
{
    public static async Task RunAsync(AppConfig config, ConfigService configService)
    {
        AnsiConsole.Clear();
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

        // 5. System Prompt
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]System Prompt (leave empty to keep current):[/]");
        AnsiConsole.MarkupLine($"[dim]Current: {config.SystemPrompt}[/]");
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
