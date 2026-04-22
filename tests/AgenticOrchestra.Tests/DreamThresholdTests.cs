using Xunit;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticOrchestra.Models;

namespace AgenticOrchestra.Tests;

/// <summary>
/// Tests that the LastDreamTelemetryCount field persists correctly across
/// JSON serialization round-trips, and that old session files without the field
/// deserialize safely to 0.
/// </summary>
public class DreamThresholdTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Roundtrip_PreservesLastDreamTelemetryCount()
    {
        var original = new SessionData
        {
            LastDreamTelemetryCount = 7,
            ProjectStateSummary = "Test project"
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<SessionData>(json, JsonOptions)!;

        Assert.Equal(7, deserialized.LastDreamTelemetryCount);
    }

    [Fact]
    public void MissingField_DefaultsToZero()
    {
        // Simulates an old session file that doesn't have LastDreamTelemetryCount
        var json = """
        {
            "projectStateSummary": "Legacy project",
            "operations": [],
            "telemetryLog": [],
            "dreamLog": []
        }
        """;

        var deserialized = JsonSerializer.Deserialize<SessionData>(json, JsonOptions)!;

        Assert.Equal(0, deserialized.LastDreamTelemetryCount);
    }

    [Fact]
    public void ThresholdDelta_CorrectlyComputed()
    {
        int totalTelemetries = 15;
        int lastDreamCount = 7;
        int threshold = 5;

        // Delta = 15 - 7 = 8, which >= 5 → should trigger
        bool shouldTrigger = (totalTelemetries - lastDreamCount) >= threshold;

        Assert.True(shouldTrigger);
    }

    [Fact]
    public void ThresholdDelta_BelowThreshold_DoesNotTrigger()
    {
        int totalTelemetries = 10;
        int lastDreamCount = 7;
        int threshold = 5;

        // Delta = 10 - 7 = 3, which < 5 → should not trigger
        bool shouldTrigger = (totalTelemetries - lastDreamCount) >= threshold;

        Assert.False(shouldTrigger);
    }

    [Fact]
    public void AfterRestart_SameCount_DoesNotTrigger()
    {
        // On restart, if last dream analyzed 12 and we still have 12 → delta = 0
        int totalTelemetries = 12;
        int lastDreamCount = 12;
        int threshold = 10;

        bool shouldTrigger = (totalTelemetries - lastDreamCount) >= threshold;

        Assert.False(shouldTrigger);
    }
}
