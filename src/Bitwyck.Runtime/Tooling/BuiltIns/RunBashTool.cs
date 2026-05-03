using System.Diagnostics;
using System.Text;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// Tool: <c>run_bash|&lt;command&gt;</c>
/// Runs a shell command via <c>cmd.exe /c</c> on Windows.
/// Commands must begin with one of the configured allow-listed prefixes; all others are rejected.
/// Output (stdout + stderr combined) is captured and truncated to 4 KB.
/// Execution times out after 30 seconds.
/// </summary>
public sealed class RunBashTool : ITool
{
    private const int  MaxOutputBytes = 4 * 1024; // 4 KB
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Conservative default allow-list. Override via constructor.</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedPrefixes = new[]
    {
        "echo",
        "dir",
        "git status",
        "git log",
        "dotnet --version",
        "dotnet --info",
        "dotnet --list-sdks",
        "type",
        "where",
        "ls",
    };

    private readonly IReadOnlyList<string> _allowedPrefixes;
    private readonly TimeSpan _timeout;

    /// <param name="allowedPrefixes">
    /// Command prefixes that are permitted. Comparison is case-insensitive and trims leading whitespace.
    /// </param>
    /// <param name="timeout">Execution timeout. Defaults to 30 seconds.</param>
    public RunBashTool(
        IReadOnlyList<string>? allowedPrefixes = null,
        TimeSpan?              timeout         = null)
    {
        _allowedPrefixes = allowedPrefixes ?? DefaultAllowedPrefixes;
        _timeout         = timeout         ?? DefaultTimeout;
    }

    public string Name           => "run_bash";
    public string Description    => "Runs an allow-listed shell command and returns combined stdout+stderr (max 4 KB, 30-second timeout).";
    public string ArgumentSchema => "command";

    public async Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (arguments.Count < 1 || string.IsNullOrWhiteSpace(arguments[0]))
            return ToolResult.Fail(Name, "Missing required argument: <command>.");

        var command = arguments[0].Trim();

        if (!IsAllowed(command, out var matchedPrefix))
        {
            var allowed = string.Join(", ", _allowedPrefixes.Select(p => $"`{p}`"));
            return ToolResult.Fail(Name,
                $"Command rejected: '{command}' does not start with an allowed prefix. Allowed: {allowed}");
        }

        return await RunCoreAsync(command, ct).ConfigureAwait(false);
    }

    private bool IsAllowed(string command, out string? matchedPrefix)
    {
        matchedPrefix = null;
        foreach (var prefix in _allowedPrefixes)
        {
            // Match if command equals prefix, or if command starts with prefix followed by whitespace.
            if (command.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith(prefix + "\t", StringComparison.OrdinalIgnoreCase))
            {
                matchedPrefix = prefix;
                return true;
            }
        }
        return false;
    }

    private async Task<ToolResult> RunCoreAsync(string command, CancellationToken callerCt)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(callerCt, timeoutCts.Token);

        var psi = new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            Arguments              = $"/c {command}",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
            CreateNoWindow         = true,
        };

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stdoutBuf.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stderrBuf.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            var combined = stdoutBuf.ToString();
            if (stderrBuf.Length > 0)
            {
                if (combined.Length > 0) combined += "\n--- stderr ---\n";
                combined += stderrBuf.ToString();
            }

            // Truncate to MaxOutputBytes.
            var bytes = Encoding.UTF8.GetBytes(combined);
            if (bytes.Length > MaxOutputBytes)
            {
                combined = Encoding.UTF8.GetString(bytes, 0, MaxOutputBytes) + "\n... [truncated]";
            }

            return process.ExitCode == 0
                ? ToolResult.Ok(Name, combined.TrimEnd())
                : ToolResult.Fail(Name, $"Exit code {process.ExitCode}.\n{combined.TrimEnd()}");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return ToolResult.Fail(Name, $"Command timed out after {_timeout.TotalSeconds:F0} seconds.");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail(Name, "Operation cancelled by caller.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(Name, $"Failed to start process: {ex.Message}");
        }
    }
}
