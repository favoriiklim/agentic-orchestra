using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticOrchestra.Models;

namespace AgenticOrchestra.Services;

/// <summary>
/// Service for interacting with a localized Ollama instance.
/// Connects via REST API (port 11434 by default).
/// </summary>
public sealed class OllamaAgent
{
    private readonly AppConfig _config;
    private readonly HttpClient _httpClient;

    public OllamaAgent(AppConfig config)
    {
        _config = config;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.Ollama.Endpoint),
            Timeout = TimeSpan.FromSeconds(config.Ollama.TimeoutSeconds)
        };
    }

    /// <summary>
    /// Checks if the Ollama endpoint is reachable and responsive.
    /// Uses a shorter timeout to prevent long delays during fallback.
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await _httpClient.GetAsync("/api/version", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            // Network error, connection refused, or timeout
            return false;
        }
    }

    /// <summary>
    /// Retrieves a list of installed models from the local Ollama instance.
    /// </summary>
    public async Task<List<string>> GetModelsAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _httpClient.GetFromJsonAsync<OllamaTagsResponse>("/api/tags", cts.Token);
            return response?.Models?.Select(m => m.Name).ToList() ?? new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Sends a prompt along with the conversation history to Ollama.
    /// Returns the generated response string.
    /// </summary>
    public async Task<string> SendPromptAsync(List<ChatMessage> history)
    {
        if (history == null || history.Count == 0)
        {
            throw new ArgumentException("Conversation history cannot be empty.");
        }

        var requestBody = new
        {
            model = _config.Ollama.Model,
            messages = history,
            stream = false // We request the entire response object at once for simplicity in MVP
        };

        var response = await _httpClient.PostAsJsonAsync("/api/chat", requestBody);
        
        // --- DEĞİŞTİRİLEN KISIM: 500 Hatasının Detayını Görmek İçin ---
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync();
            throw new Exception($"Ollama API Error ({(int)response.StatusCode}): {errorText}");
        }
        // --------------------------------------------------------------

        var responseData = await response.Content.ReadFromJsonAsync<OllamaChatResponse>();
        
        return responseData?.Message?.Content ?? string.Empty;
    }
}

// ── Models for internal JSON deserialization ──

internal class OllamaTagsResponse
{
    [JsonPropertyName("models")]
    public List<OllamaModel> Models { get; set; } = new();
}

internal class OllamaModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

internal class OllamaChatResponse
{
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }
}