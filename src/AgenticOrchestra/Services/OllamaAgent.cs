using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticOrchestra.Models;

namespace AgenticOrchestra.Services;

/// <summary>
/// Layer 1 Agent: Local Ollama/Gemma instance.
/// In the 3-layer hierarchy, this acts as a COMMUNICATION INTERFACE ONLY:
///   - Classifies user prompts (simple vs complex)
///   - Presents ManagerTelemetry JSON as human-readable text
///   - Never performs actual operational planning or coding tasks
/// </summary>
public sealed class OllamaAgent
{
    private readonly AppConfig _config;
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions TelemetryJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
        response.EnsureSuccessStatusCode();

        var responseData = await response.Content.ReadFromJsonAsync<OllamaChatResponse>();
        
        return responseData?.Message?.Content ?? string.Empty;
    }

    // ── Layer 1 Specialized Methods (3-Layer Hierarchy) ────────────

    /// <summary>
    /// Classifies the user's prompt to determine whether it should be handled
    /// locally (simple greetings/questions) or delegated to the Web Manager AI.
    /// Returns a ManagerTaskRequest with the classification, or null if the task is simple.
    /// </summary>
    public async Task<ManagerTaskRequest?> ClassifyPromptAsync(string userPrompt, string projectContext)
    {
        var classificationPrompt = new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = ChatRole.System,
                Content = @"You are a task classifier for an AI orchestration system. Your ONLY job is to categorize user input.

RULES:
- Respond with EXACTLY one JSON object, nothing else.
- Format: {""category"": ""<category>"", ""reasoning"": ""<one sentence>""}
- Categories:
  - ""simple"": Greetings, small talk, thank you messages, asking what you can do, basic questions answerable in one sentence.
  - ""code"": Writing, debugging, refactoring, or reviewing code. Creating projects. Build/deployment tasks.
  - ""research"": Looking up documentation, comparing technologies, investigating errors, searching for solutions.
  - ""debug"": Diagnosing runtime errors, log analysis, troubleshooting system issues.
  - ""general"": Anything complex that doesn't fit above: planning, multi-step analysis, architecture design.

Respond ONLY with the JSON. No markdown fences, no extra text."
            },
            new ChatMessage
            {
                Role = ChatRole.User,
                Content = userPrompt
            }
        };

        try
        {
            var classificationResult = await SendPromptAsync(classificationPrompt);
            
            // Attempt to parse the classification JSON
            using var doc = JsonDocument.Parse(classificationResult);
            var category = doc.RootElement.GetProperty("category").GetString() ?? "general";

            if (category.Equals("simple", StringComparison.OrdinalIgnoreCase))
            {
                return null; // Signal: handle locally, don't delegate
            }

            return new ManagerTaskRequest
            {
                UserPrompt = userPrompt,
                TaskCategory = category,
                ProjectContext = projectContext
            };
        }
        catch
        {
            // If classification fails (JSON parse error, etc.), default to delegating
            // Safer to send to the Manager than to handle a complex task locally
            return new ManagerTaskRequest
            {
                UserPrompt = userPrompt,
                TaskCategory = "general",
                ProjectContext = projectContext
            };
        }
    }

    /// <summary>
    /// Takes a raw ManagerTelemetry JSON object and asks the local model to present it
    /// as clean, human-readable text for the user. This is the final step in the
    /// Normal Mode pipeline: Manager produces JSON → Local Model translates to prose.
    /// </summary>
    public async Task<string> PresentTelemetryAsync(ManagerTelemetry telemetry)
    {
        var telemetryJson = JsonSerializer.Serialize(telemetry, TelemetryJsonOptions);

        var presentationPrompt = new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = ChatRole.System,
                Content = @"You are a status reporter for an AI engineering system. You receive a JSON telemetry report from the Manager AI that executed a task. Your job is to translate this JSON into clear, readable text for the human user.

RULES:
1. Present the outcome naturally — do NOT just dump the JSON fields.
2. If workers were spawned, briefly mention what each did.
3. If errors occurred, explain them clearly.
4. If files were written or commands executed, highlight the key results.
5. End with the final outcome or next steps.
6. Be concise but informative. Use bullet points for multiple items.
7. Do NOT wrap your response in code blocks or JSON."
            },
            new ChatMessage
            {
                Role = ChatRole.User,
                Content = $"Present this telemetry report to the user:\n\n{telemetryJson}"
            }
        };

        try
        {
            return await SendPromptAsync(presentationPrompt);
        }
        catch
        {
            // If presentation fails, return raw telemetry as fallback
            return $"[Telemetry Report]\nTask: {telemetry.TaskExecuted}\nOutcome: {telemetry.FinalOutcome}\nWorkers: {string.Join(", ", telemetry.WorkersSpawned)}\nErrors: {telemetry.ErrorsHandled}";
        }
    }

    /// <summary>
    /// Simple direct response for "simple" category prompts that don't need
    /// the full orchestration pipeline. Used in Normal Mode only.
    /// </summary>
    public async Task<string> RespondDirectlyAsync(string userPrompt, List<ChatMessage> history)
    {
        // Add the current prompt to a copy of history for context
        var messages = new List<ChatMessage>(history)
        {
            new ChatMessage { Role = ChatRole.User, Content = userPrompt }
        };

        return await SendPromptAsync(messages);
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
