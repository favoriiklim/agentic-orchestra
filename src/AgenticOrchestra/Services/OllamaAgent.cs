using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticOrchestra.Models;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// Service for interacting with a localized Ollama instance.
/// Connects via REST API (port 11434 by default).
/// Uses STREAMING to prevent timeout on large models and provide real-time feedback.
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
            // High timeout only as a safety net - streaming keeps the connection alive
            Timeout = TimeSpan.FromMinutes(10)
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
    /// Uses STREAMING mode: reads tokens incrementally so the connection never times out.
    /// The optional onToken callback is invoked for each received token fragment.
    /// </summary>
    public async Task<string> SendPromptAsync(List<ChatMessage> history, Action<string>? onToken = null)
    {
        if (history == null || history.Count == 0)
        {
            throw new ArgumentException("Conversation history cannot be empty.");
        }

        var requestBody = new
        {
            model = _config.Ollama.Model,
            messages = history,
            stream = true
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = jsonContent },
            HttpCompletionOption.ResponseHeadersRead
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync();
            throw new Exception($"Ollama API Error ({(int)response.StatusCode}): {errorText}");
        }

        // ── Stream the response token by token ──
        var fullResponse = new StringBuilder();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var chunk = JsonSerializer.Deserialize<OllamaStreamChunk>(line);
                if (chunk?.Message?.Content != null)
                {
                    fullResponse.Append(chunk.Message.Content);
                    onToken?.Invoke(chunk.Message.Content); // Live callback
                }

                if (chunk?.Done == true)
                {
                    break;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return fullResponse.ToString();
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

/// <summary>
/// Represents a single chunk in the Ollama streaming response.
/// Each line of the NDJSON stream contains one of these.
/// </summary>
internal class OllamaStreamChunk
{
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }
}