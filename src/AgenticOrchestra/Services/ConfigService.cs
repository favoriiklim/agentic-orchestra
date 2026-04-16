using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticOrchestra.Models;

namespace AgenticOrchestra.Services;

/// <summary>
/// Manages loading and saving the application configuration to a JSON file.
/// Config location is resolved via Environment.GetFolderPath for cross-platform compatibility.
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Gets the absolute path to the configuration directory.
    /// Windows: %APPDATA%\AgenticOrchestra\
    /// Linux:   ~/.config/AgenticOrchestra/
    /// </summary>
    public static string ConfigDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AgenticOrchestra");

    /// <summary>
    /// Gets the absolute path to the config.json file.
    /// </summary>
    public static string ConfigFilePath =>
        Path.Combine(ConfigDirectory, "config.json");

    /// <summary>
    /// Loads the configuration from disk. If the file doesn't exist,
    /// creates a new one with default values and returns it.
    /// </summary>
    public async Task<AppConfig> LoadAsync()
    {
        if (!File.Exists(ConfigFilePath))
        {
            var defaultConfig = new AppConfig();
            await SaveAsync(defaultConfig);
            return defaultConfig;
        }

        try
        {
            var json = await File.ReadAllTextAsync(ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return config ?? new AppConfig();
        }
        catch (JsonException)
        {
            // If the config is corrupted, back it up and create a fresh one
            var backupPath = ConfigFilePath + $".backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(ConfigFilePath, backupPath, overwrite: true);

            var freshConfig = new AppConfig();
            await SaveAsync(freshConfig);
            return freshConfig;
        }
    }

    /// <summary>
    /// Saves the configuration to disk. Creates the directory if it doesn't exist.
    /// </summary>
    public async Task SaveAsync(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);

        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(ConfigFilePath, json);
    }

    /// <summary>
    /// Returns the path used for the Playwright persistent browser profile.
    /// Windows: %LOCALAPPDATA%\AgenticOrchestra\browser-profile\
    /// Linux:   ~/.local/share/AgenticOrchestra/browser-profile/
    /// </summary>
    public static string BrowserProfilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgenticOrchestra",
            "browser-profile");
}
