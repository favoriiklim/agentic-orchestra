using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace AgenticOrchestra.Services;

/// <summary>
/// A persistent shell engine allowing the system to execute developer scripts directly.
/// Handles async reading with timeouts and thread-safe synchronization to prevent stream collisions.
/// Supports CancellationToken for deterministic cancellation via process-tree kill + restart.
/// </summary>
public sealed class NativeTerminalService : IDisposable
{
    private Process? _process;
    private readonly string _marker = "__COMMAND_COMPLETED__";
    private readonly bool _isWindows;
    private readonly int _commandTimeoutSeconds;
    
    // ── REACTIVE STATE ──
    private readonly StringBuilder _accumulatedOutput = new();
    private readonly object _outputLock = new();
    private TaskCompletionSource<bool> _markerReceivedSignal = new();
    
    // ── SYNCHRONIZATION ──
    private readonly SemaphoreSlim _lock = new(1, 1);

    public NativeTerminalService(int commandTimeoutSeconds = 30)
    {
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _commandTimeoutSeconds = commandTimeoutSeconds;
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
    /// Thread-safe via SemaphoreSlim. Supports CancellationToken for user-initiated cancellation.
    /// On cancellation or timeout, the shell process is killed and restarted for deterministic cleanup.
    /// </summary>
    public async Task<string> ExecuteCommandAsync(string command, CancellationToken ct = default)
    {
        // ── SYNC LOCK ──
        await _lock.WaitAsync(ct);
        try
        {
            AnsiConsole.MarkupLine($"[dim yellow]Executing command...[/]");

            // ── SETTLING PERIOD ──
            await Task.Delay(100, ct);

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

            string fullCommand = _isWindows 
                ? $"{command} 2>&1\nif ($?) {{ Write-Output '__EXITCODE__:0' }} else {{ Write-Output '__EXITCODE__:1' }}\nWrite-Output '{_marker}'" 
                : $"{command} 2>&1\necho '__EXITCODE__:$?'\necho '{_marker}'";

            await _process!.StandardInput.WriteLineAsync(fullCommand);
            await _process.StandardInput.FlushAsync();

            // Link user cancellation with the timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_commandTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            try
            {
                // Wait for the background reader to signal completion or for cancellation
                await Task.WhenAny(
                    _markerReceivedSignal.Task, 
                    Task.Delay(Timeout.Infinite, linkedCts.Token)
                );
            }
            catch (OperationCanceledException)
            {
                // Fall through to kill logic below
            }

            // If marker was not received, the command timed out or was cancelled
            if (!_markerReceivedSignal.Task.IsCompleted)
            {
                bool wasCancelled = ct.IsCancellationRequested;
                string reason = wasCancelled ? "User cancellation" : "Timeout";
                AnsiConsole.MarkupLine($"[dim red]{reason}. Killing shell process and restarting...[/]");
                await KillAndRestartAsync();

                string partialResult;
                lock (_outputLock)
                {
                    partialResult = _accumulatedOutput.ToString().Trim();
                }
                
                if (wasCancelled)
                {
                    throw new OperationCanceledException("Terminal command cancelled by user.", ct);
                }

                return string.IsNullOrWhiteSpace(partialResult) 
                    ? "(Error: Command timed out or was interrupted)" 
                    : partialResult + "\n(Error: Command timed out or was interrupted)";
            }

            string result;
            lock (_outputLock)
            {
                result = _accumulatedOutput.ToString().Trim();
            }

            AnsiConsole.MarkupLine(string.IsNullOrWhiteSpace(result) ? "[dim]Command completed with no output.[/]" : "[dim green]Command executed.[/]");
            
            return string.IsNullOrWhiteSpace(result) ? "(Success: No output returned)" : result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Kills the active shell process tree and starts a fresh one.
    /// Uses taskkill /t /f /pid on Windows for reliable process-tree termination.
    /// </summary>
    private async Task KillAndRestartAsync()
    {
        if (_process != null && !_process.HasExited)
        {
            int pid = _process.Id;

            try
            {
                if (_isWindows)
                {
                    // Kill the entire process tree
                    using var killer = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/t /f /pid {pid}",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    if (killer != null) await killer.WaitForExitAsync();
                }
                else
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { /* Process may have already exited */ }

            try { _process.Dispose(); } catch { }
            _process = null;
        }

        await Task.Delay(100); // Brief settle
        InitializeProcess();
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
