using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AgenticOrchestra.Models;

namespace AgenticOrchestra.Services;

/// <summary>The physical tool a guard decision applies to.</summary>
public enum ToolKind
{
    Terminal,
    FileWrite,
    FileRead
}

/// <summary>What the caller should do with a proposed tool call.</summary>
public enum GuardDecision
{
    /// <summary>Run it without asking.</summary>
    Allow,

    /// <summary>Ask the human first.</summary>
    RequireApproval,

    /// <summary>Refuse; do not offer an override.</summary>
    Deny
}

/// <summary>Outcome of a guard evaluation, with a human-readable reason.</summary>
public readonly record struct GuardResult(GuardDecision Decision, string Reason)
{
    public static GuardResult Allow(string reason = "") => new(GuardDecision.Allow, reason);
    public static GuardResult Ask(string reason) => new(GuardDecision.RequireApproval, reason);
    public static GuardResult Deny(string reason) => new(GuardDecision.Deny, reason);
}

/// <summary>
/// Decides whether a tool call proposed by an AI may touch the machine.
///
/// Deliberately free of console I/O so the policy can be unit tested; the
/// interactive prompt lives in <see cref="ToolExecutionService"/>.
/// </summary>
public sealed class SafetyGuard
{
    private readonly SafetySettings _settings;
    private readonly string _workspaceRoot;
    private readonly List<Regex> _blocked;
    private readonly List<Regex> _autoApprove;

    /// <summary>Commands and paths the user approved with "always allow" this session.</summary>
    private readonly HashSet<string> _sessionApprovals = new(StringComparer.OrdinalIgnoreCase);

    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public SafetyGuard(SafetySettings settings, string? workspaceRootOverride = null)
    {
        _settings = settings;

        var root = workspaceRootOverride
                   ?? (string.IsNullOrWhiteSpace(settings.WorkspaceRoot)
                       ? Directory.GetCurrentDirectory()
                       : settings.WorkspaceRoot);
        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        _blocked = Compile(settings.BlockedCommandPatterns);
        _autoApprove = Compile(settings.AutoApproveCommandPatterns);
    }

    /// <summary>The absolute directory file writes are confined to.</summary>
    public string WorkspaceRoot => _workspaceRoot;

    public ApprovalMode Mode => _settings.ApprovalMode;

    /// <summary>True when markdown code blocks may be rewritten into executable tokens.</summary>
    public bool NormalizeCodeBlocks => _settings.NormalizeCodeBlocks;

    private static List<Regex> Compile(IEnumerable<string> patterns)
    {
        var compiled = new List<Regex>();
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                compiled.Add(new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled));
            }
            catch (ArgumentException)
            {
                // A malformed user-supplied pattern must not take the app down.
                // Skipping it is safe for auto-approve; for the blocklist the
                // remaining patterns plus the approval prompt still apply.
            }
        }
        return compiled;
    }

    /// <summary>Evaluates a proposed terminal command.</summary>
    public GuardResult EvaluateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return GuardResult.Deny("Empty command.");

        // The blocklist wins in every mode, including Auto.
        foreach (var rx in _blocked)
        {
            if (rx.IsMatch(command))
                return GuardResult.Deny($"Matches blocked pattern: {rx}");
        }

        if (_settings.ApprovalMode == ApprovalMode.ReadOnly)
            return GuardResult.Deny("Read-only mode: terminal execution is disabled.");

        if (_settings.ApprovalMode == ApprovalMode.Auto)
            return GuardResult.Allow("Auto-approval mode.");

        if (_sessionApprovals.Contains(SessionKey(ToolKind.Terminal, command)))
            return GuardResult.Allow("Approved earlier this session.");

        foreach (var rx in _autoApprove)
        {
            if (rx.IsMatch(command))
                return GuardResult.Allow("Read-only command.");
        }

        return GuardResult.Ask("Command changes machine state.");
    }

    /// <summary>Evaluates a proposed file write.</summary>
    public GuardResult EvaluateFileWrite(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return GuardResult.Deny("Empty path.");

        if (_settings.ApprovalMode == ApprovalMode.ReadOnly)
            return GuardResult.Deny("Read-only mode: file writes are disabled.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, _workspaceRoot);
        }
        catch (Exception ex)
        {
            return GuardResult.Deny($"Unresolvable path: {ex.Message}");
        }

        if (_settings.ConfineFileWritesToWorkspace && !IsInsideWorkspace(fullPath))
            return GuardResult.Deny($"Path escapes the workspace ({_workspaceRoot}).");

        if (_settings.ApprovalMode == ApprovalMode.Auto)
            return GuardResult.Allow("Auto-approval mode.");

        if (_sessionApprovals.Contains(SessionKey(ToolKind.FileWrite, fullPath)))
            return GuardResult.Allow("Approved earlier this session.");

        return GuardResult.Ask("Writes to disk.");
    }

    /// <summary>Evaluates a proposed file read. Reads are permitted in every mode.</summary>
    public GuardResult EvaluateFileRead(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? GuardResult.Deny("Empty path.")
            : GuardResult.Allow();

    /// <summary>
    /// Returns true when <paramref name="fullPath"/> sits inside the workspace root.
    /// Compares on directory boundaries so "C:\work-secrets" is not treated as
    /// living inside "C:\work".
    /// </summary>
    public bool IsInsideWorkspace(string fullPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));

        if (normalized.Equals(_workspaceRoot, PathComparison))
            return true;

        var rootWithSeparator = _workspaceRoot + Path.DirectorySeparatorChar;
        return normalized.StartsWith(rootWithSeparator, PathComparison);
    }

    /// <summary>Records an "always allow this session" decision from the user.</summary>
    public void RememberApproval(ToolKind kind, string target)
    {
        if (kind == ToolKind.FileWrite)
        {
            try { target = Path.GetFullPath(target, _workspaceRoot); }
            catch { /* fall back to the raw string */ }
        }
        _sessionApprovals.Add(SessionKey(kind, target));
    }

    /// <summary>Clears session approvals — called when a new task starts.</summary>
    public void ResetSessionApprovals() => _sessionApprovals.Clear();

    private static string SessionKey(ToolKind kind, string target) => $"{kind}:{target}";
}
