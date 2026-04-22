namespace AgenticOrchestra.Services;

/// <summary>
/// Determines what action to take on each Ctrl+C press.
/// Extracted as a testable policy class with injectable clock.
/// </summary>
public sealed class CancelKeyPolicy
{
    private readonly Func<DateTime> _clock;
    private DateTime? _lastPressTime;

    /// <summary>Set to true when the user presses Ctrl+C twice within the grace window.</summary>
    public bool ForceExitRequested { get; private set; }

    /// <summary>Grace period for the second Ctrl+C to trigger force-exit.</summary>
    public TimeSpan GraceWindow { get; } = TimeSpan.FromSeconds(2);

    public CancelKeyPolicy() : this(() => DateTime.UtcNow) { }

    /// <summary>Constructor with injectable clock for deterministic testing.</summary>
    public CancelKeyPolicy(Func<DateTime> clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Call on each Ctrl+C press. Returns the action to take.
    /// </summary>
    public CancelAction HandlePress()
    {
        var now = _clock();

        if (_lastPressTime.HasValue && (now - _lastPressTime.Value) <= GraceWindow)
        {
            ForceExitRequested = true;
            _lastPressTime = null;
            return CancelAction.ForceExit;
        }

        _lastPressTime = now;
        return CancelAction.CancelTask;
    }

    /// <summary>Resets state after a successful task cancellation.</summary>
    public void Reset()
    {
        _lastPressTime = null;
        ForceExitRequested = false;
    }
}

public enum CancelAction
{
    CancelTask,
    ForceExit
}
