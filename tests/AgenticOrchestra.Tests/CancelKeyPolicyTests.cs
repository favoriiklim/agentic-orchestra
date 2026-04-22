using Xunit;
using AgenticOrchestra.Services;

namespace AgenticOrchestra.Tests;

/// <summary>
/// Tests the CancelKeyPolicy: first press cancels, second within 2s force-exits,
/// window expiration resets to cancel behavior.
/// </summary>
public class CancelKeyPolicyTests
{
    [Fact]
    public void FirstPress_ReturnsCancelTask()
    {
        var now = DateTime.UtcNow;
        var policy = new CancelKeyPolicy(() => now);

        var result = policy.HandlePress();

        Assert.Equal(CancelAction.CancelTask, result);
        Assert.False(policy.ForceExitRequested);
    }

    [Fact]
    public void SecondPressWithinWindow_ReturnsForceExit()
    {
        var time = DateTime.UtcNow;
        var policy = new CancelKeyPolicy(() => time);

        policy.HandlePress(); // first

        // Advance 1 second (within 2s window)
        time = time.AddSeconds(1);
        var result = policy.HandlePress(); // second

        Assert.Equal(CancelAction.ForceExit, result);
        Assert.True(policy.ForceExitRequested);
    }

    [Fact]
    public void SecondPressOutsideWindow_ReturnsCancelTask()
    {
        var time = DateTime.UtcNow;
        var clock = () => time;
        var policy = new CancelKeyPolicy(clock);

        policy.HandlePress(); // first

        // Advance 3 seconds (outside 2s window)
        time = time.AddSeconds(3);
        var result = policy.HandlePress();

        Assert.Equal(CancelAction.CancelTask, result);
        Assert.False(policy.ForceExitRequested);
    }

    [Fact]
    public void Reset_ClearsForceExitFlag()
    {
        var time = DateTime.UtcNow;
        var policy = new CancelKeyPolicy(() => time);

        policy.HandlePress();
        time = time.AddMilliseconds(500);
        policy.HandlePress(); // triggers ForceExit

        Assert.True(policy.ForceExitRequested);

        policy.Reset();

        Assert.False(policy.ForceExitRequested);
    }

    [Fact]
    public void PressExactlyAtWindowBoundary_ReturnsForceExit()
    {
        var time = DateTime.UtcNow;
        var policy = new CancelKeyPolicy(() => time);

        policy.HandlePress();

        // Advance exactly 2 seconds (at boundary — should still count as within)
        time = time.AddSeconds(2);
        var result = policy.HandlePress();

        Assert.Equal(CancelAction.ForceExit, result);
    }

    [Fact]
    public void ThirdPressAfterForceExit_ReturnsCancelTask()
    {
        var time = DateTime.UtcNow;
        var policy = new CancelKeyPolicy(() => time);

        policy.HandlePress();
        time = time.AddSeconds(1);
        policy.HandlePress(); // ForceExit, clears lastPressTime

        // Third press after force exit triggered — no lastPressTime, so this is a fresh first press
        time = time.AddSeconds(1);
        var result = policy.HandlePress();

        Assert.Equal(CancelAction.CancelTask, result);
    }
}
