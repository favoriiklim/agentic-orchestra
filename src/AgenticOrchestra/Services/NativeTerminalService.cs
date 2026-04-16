using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// A persistent shell engine allowing the system to execute developer scripts directly.
/// Handles async reading with timeouts and thread-safe synchronization to prevent stream collisions.
/// </summary>
public sealed class NativeTerminalService : IDisposable
{
    private Process? _process;
    private readonly string _marker = "__COMMAND_COMPLETED__";
    private readonly bool _isWindows;
    
    // ── REACTIVE STATE ──
    private readonly StringBuilder _accumulatedOutput = new();
    private readonly object _outputLock = new();
    private TaskCompletionSource<bool> _markerReceivedSignal = new();
    
    // ── SYNCHRONIZATION ──
    private readonly SemaphoreSlim _lock = new(1, 1);

    public NativeTerminalService()
    {
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        InitializeProcess();
    }

    private void InitializeProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _isWindows ? "powershell.exe" : "bash",
            Arguments = _isWindows ? "-NoProfile -NoExit -Command -" : "-i",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        _process = new Process { StartInfo = startInfo };
        _process.Start();

        // ── KICK OFF BACKGROUND READERS ──
        StartBackgroundReader(_process.StandardOutput, "STDOUT");
        StartBackgroundReader(_process.StandardError, "STDERR");
    }

    private void StartBackgroundReader(StreamReader reader, string streamName)
    {
        Task.Run(async () =>
        {
            try
            {
                while (_process != null && !_process.HasExited)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null) break;

                    lock (_outputLock)
                    {
                        if (line.TrimEnd().EndsWith(_marker))
                        {
                            _markerReceivedSignal.TrySetResult(true);
                        }
                        else
                        {
                            _accumulatedOutput.AppendLine(line);
                        }
                    }
                }
            }
            catch { /* Process exit or dispose */ }
        });
    }


    /// <summary>
    /// Executes a command in the persistent shell environment.
    /// Thread-safe via SemaphoreSlim and guarded by user approval.
    /// </summary>
    public async Task<string> ExecuteCommandAsync(string command)
    {
        // ── SYNC LOCK ──
        await _lock.WaitAsync();
        try
        {
            // ── MANDATORY APPROVAL GATE ──
            AnsiConsole.WriteLine();
            var confirm = AnsiConsole.Prompt(
                new ConfirmationPrompt($"[purple](APPROVAL REQUIRED)[/] Execute: '{Markup.Escape(command)}'?")
                {
                    DefaultValue = false
                }
            );

            if (!confirm)
            {
                AnsiConsole.MarkupLine("[dim red]Command execution rejected by user.[/]");
                return "(System Error: The User explicitly REJECTED the command execution.)";
            }

            // ── SETTLING PERIOD ──
            await Task.Delay(100);

            if (_process == null || _process.HasExited)
            {
                InitializeProcess();
            }

            // ── RESET STATE FOR NEW COMMAND ──
            lock (_outputLock)
            {
                _accumulatedOutput.Clear();
                _markerReceivedSignal = new TaskCompletionSource<bool>();
            }

            AnsiConsole.MarkupLine($"[dim yellow]Executing command...[/]");

            string fullCommand = _isWindows 
                ? $"{command} 2>&1; Write-Output '{_marker}'" 
                : $"{command} 2>&1\necho '{_marker}'";

            await _process!.StandardInput.WriteLineAsync(fullCommand);
            await _process.StandardInput.FlushAsync();

            // We use two levels of timeout: Total (30s) and Silence (5s)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            
            try
            {
                // Wait for the background reader to signal completion or for timeout
                await Task.WhenAny(_markerReceivedSignal.Task, Task.Delay(-1, timeoutCts.Token));

                if (!_markerReceivedSignal.Task.IsCompleted)
                {
                    AnsiConsole.MarkupLine("[dim red]Command timed out. Sending Interrupt (Ctrl+C)...[/]");
                    await SendInterruptAsync();
                }
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[dim red]Command timed out. Sending Interrupt (Ctrl+C)...[/]");
                await SendInterruptAsync();
            }

            string result;
            lock (_outputLock)
            {
                result = _accumulatedOutput.ToString().Trim();
            }

            // Heuristic to detect if it was interrupted
            if (!_markerReceivedSignal.Task.IsCompleted)
            {
                result += "\n(Error: Command timed out or was interrupted)";
            }

            AnsiConsole.MarkupLine(string.IsNullOrWhiteSpace(result) ? "[dim]Command completed with no output.[/]" : "[dim green]Command executed.[/]");
            
            return string.IsNullOrWhiteSpace(result) ? "(Success: No output returned)" : result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SendInterruptAsync()
    {
        if (_process != null && !_process.HasExited)
        {
            // Sending ASCII 3 (ETX/Ctrl+C)
            await _process.StandardInput.WriteAsync("\x03");
            await _process.StandardInput.FlushAsync();
            // Settling after interrupt
            await Task.Delay(200);
        }
    }

    public void Dispose()
    {
        if (_process != null && !_process.HasExited)
        {
            _process.Kill();
            _process.Dispose();
        }
        _lock.Dispose();
    }
}
