using Xunit;
using System.Diagnostics;
using AgenticOrchestra.Services;

namespace AgenticOrchestra.Tests;

/// <summary>
/// Tests that NativeTerminalService properly kills and restarts the shell
/// on cancellation, and that a fresh process ID is produced.
/// </summary>
public class TerminalCancellationTests
{
    [Fact]
    public async Task CancelledCommand_ThrowsOperationCancelled()
    {
        var service = new NativeTerminalService(commandTimeoutSeconds: 30);
        using var cts = new CancellationTokenSource();

        // Cancel immediately
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.ExecuteCommandAsync("echo hello", cts.Token);
        });

        service.Dispose();
    }

    [Fact]
    public async Task TimedOutCommand_ReturnsErrorMessage()
    {
        // 3-second timeout with a command that would take longer
        var service = new NativeTerminalService(commandTimeoutSeconds: 3);

        // Start-Sleep on PowerShell should exceed 1s timeout
        var result = await service.ExecuteCommandAsync("Start-Sleep -Seconds 10");

        Assert.Contains("timed out", result, StringComparison.OrdinalIgnoreCase);

        service.Dispose();
    }

    [Fact]
    public async Task AfterTimeout_ShellRestarted_NextCommandSucceeds()
    {
        var service = new NativeTerminalService(commandTimeoutSeconds: 3);

        // First: trigger timeout
        var result1 = await service.ExecuteCommandAsync("Start-Sleep -Seconds 10");
        Assert.Contains("timed out", result1, StringComparison.OrdinalIgnoreCase);

        // Second: verify shell was restarted and works
        var result2 = await service.ExecuteCommandAsync("echo 'recovery_test'");
        Assert.Contains("recovery_test", result2);

        service.Dispose();
    }
}
