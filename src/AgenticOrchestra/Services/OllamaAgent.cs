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
    /// Checks whether Layer 1 can actually serve a request.
    ///
    /// A reachable server is not enough: Ollama answers /api/version happily while
    /// holding zero models, and then fails every /api/chat with 404. Treating that
    /// as "available" sent the pipeline into normal mode and crashed the app, so
    /// the configured model must be present too.
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        var health = await CheckHealthAsync();
        return health.IsUsable;
    }

    /// <summary>
    /// Probes the local Ollama instance and reports exactly what is wrong,
    /// so the UI can tell the user what to do about it.
    /// </summary>
    public async Task<OllamaHealth> CheckHealthAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await _httpClient.GetAsync("/api/version", cts.Token);
            if (!response.IsSuccessStatusCode)
                return OllamaHealth.Down($"Ollama replied {(int)response.StatusCode} to /api/version.");
        }
        catch (Exception ex)
        {
            // Network error, connection refused, or timeout
            return OllamaHealth.Down($"Cannot reach Ollama at {_config.Ollama.Endpoint} ({ex.GetType().Name}).");
        }

        var models = await GetModelsAsync();

        if (models.Count == 0)
        {
            return new OllamaHealth(
                ServerUp: true,
                ModelPresent: false,
                InstalledModels: models,
                Message: $"Ollama is running but has no models installed. Run: ollama pull {_config.Ollama.Model}");
        }

        if (!ModelMatches(models, _config.Ollama.Model))
        {
            return new OllamaHealth(
                ServerUp: true,
                ModelPresent: false,
                InstalledModels: models,
                Message: $"Model '{_config.Ollama.Model}' is not installed. " +
                         $"Run: ollama pull {_config.Ollama.Model} — or pick one of: {string.Join(", ", models)}");
        }

        return new OllamaHealth(true, true, models, "Ready.");
    }

    /// <summary>
    /// Compares a configured model name against installed tags. Ollama reports
    /// "llama3.2:latest", so a bare "llama3.2" has to match it.
    /// </summary>
    internal static bool ModelMatches(IEnumerable<string> installed, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return false;

        var wanted = configured.Trim();
        var wantedWithTag = wanted.Contains(':') ? wanted : wanted + ":latest";

        return installed.Any(m =>
            m.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
            m.Equals(wantedWithTag, StringComparison.OrdinalIgnoreCase));
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

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync("/api/chat", requestBody);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new OllamaException(
                $"Could not reach Ollama at {_config.Ollama.Endpoint}. Is it running? ({ex.Message})", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Ollama explains itself in the body ({"error":"model 'x' not found"});
            // EnsureSuccessStatusCode would throw that detail away.
            var body = await SafeReadBodyAsync(response);
            throw new OllamaException(DescribeFailure(response.StatusCode, body));
        }

        var responseData = await response.Content.ReadFromJsonAsync<OllamaChatResponse>();

        return responseData?.Message?.Content ?? string.Empty;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
    {
        try { return await response.Content.ReadAsStringAsync(); }
        catch { return string.Empty; }
    }

    /// <summary>Turns an Ollama error response into something the user can act on.</summary>
    private string DescribeFailure(System.Net.HttpStatusCode status, string body)
    {
        var detail = ExtractError(body);

        if (status == System.Net.HttpStatusCode.NotFound)
        {
            return $"Ollama does not have the model '{_config.Ollama.Model}'. " +
                   $"Install it with:  ollama pull {_config.Ollama.Model}" +
                   (string.IsNullOrWhiteSpace(detail) ? "" : $"\nOllama said: {detail}");
        }

        return $"Ollama returned {(int)status} ({status})." +
               (string.IsNullOrWhiteSpace(detail) ? "" : $" {detail}");
    }

    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString() ?? string.Empty;
        }
        catch (JsonException) { /* not JSON — fall through */ }

        return body.Length > 300 ? body[..300] : body;
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
            var jsonContent = classificationResult.Trim();
            if (jsonContent.StartsWith("```"))
            {
                var firstNewline = jsonContent.IndexOf('\n');
                if (firstNewline > 0) jsonContent = jsonContent[(firstNewline + 1)..];
                if (jsonContent.EndsWith("```"))
                    jsonContent = jsonContent[..^3];
                jsonContent = jsonContent.Trim();
            }
            
            // Attempt to parse the classification JSON
            using var doc = JsonDocument.Parse(jsonContent);
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
        return await SendPromptAsync(history);
    }
}

/// <summary>
/// Raised when the local Ollama instance cannot serve a request. Carries a
/// message written for the user, not a stack trace.
/// </summary>
public sealed class OllamaException : Exception
{
    public OllamaException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The result of probing Ollama: whether the server answers, and whether the
/// configured model is actually installed.
/// </summary>
public sealed record OllamaHealth(
    bool ServerUp,
    bool ModelPresent,
    List<string> InstalledModels,
    string Message)
{
    /// <summary>True only when Layer 1 can genuinely handle a prompt.</summary>
    public bool IsUsable => ServerUp && ModelPresent;

    public static OllamaHealth Down(string message) => new(false, false, new List<string>(), message);

    /// <summary>Layer 1 was switched off in config — web-only mode, not a failure.</summary>
    public static OllamaHealth Disabled() =>
        new(false, false, new List<string>(), "Local AI is disabled in settings — running web-only.");
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
