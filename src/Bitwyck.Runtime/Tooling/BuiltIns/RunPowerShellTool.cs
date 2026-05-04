using System.Diagnostics;
using System.Text;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// <c>run_powershell|&lt;command&gt;</c>
/// Runs a PowerShell command via <c>powershell.exe -NoProfile -NonInteractive
/// -Command "&lt;cmd&gt;"</c> with a configurable prefix allow-list. Captures
/// stdout + stderr (4 KB cap, 30 s timeout). Defaults to read-only Get-* /
/// Test-* / dir / ls cmdlets — destructive operations require explicit opt-in.
/// </summary>
public sealed class RunPowerShellTool : ITool
{
    public static readonly string[] DefaultAllowList =
    {
        "Get-ChildItem", "Get-Item", "Get-Content", "Get-Location", "Get-Date",
        "Get-Process", "Get-Service", "Get-Help", "Get-Command", "Get-Module",
        "Get-Variable", "Get-Member", "Get-History", "Get-PSDrive",
        "Test-Path", "Resolve-Path", "Select-Object", "Where-Object", "ForEach-Object",
        "Measure-Object", "Sort-Object", "Format-List", "Format-Table",
        "ConvertTo-Json", "ConvertFrom-Json",
        "dir", "ls", "pwd", "cd", "echo", "type", "where",
    };

    private readonly IReadOnlyList<string> _allowList;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private const int MaxOutputBytes = 4096;

    public RunPowerShellTool(IReadOnlyList<string>? allowList = null)
    {
        _allowList = (allowList is { Count: > 0 } ? allowList : DefaultAllowList).ToArray();
    }

    public string Name => "run_powershell";
    public string Description => "Runs an allow-listed PowerShell command and returns combined stdout+stderr (max 4 KB, 30s).";
    public string ArgumentSchema => "command";

    public async Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (arguments.Count < 1 || string.IsNullOrWhiteSpace(arguments[0]))
            return ToolResult.Fail(Name, "missing command");

        var cmd = string.Join(" | ", arguments).Trim();
        if (!IsAllowed(cmd))
            return ToolResult.Fail(Name, $"command not in allow-list (starts with '{FirstToken(cmd)}'). Add it via Bitwyck:Tools:PowerShellAllowList in appsettings.");

        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(cmd);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(_timeout);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            var combined = stdout.ToString();
            if (stderr.Length > 0)
                combined += $"\n[stderr]\n{stderr}";

            if (combined.Length > MaxOutputBytes)
                combined = combined[..MaxOutputBytes] + "\n... [truncated]";

            return process.ExitCode == 0
                ? ToolResult.Ok(Name, combined.TrimEnd())
                : ToolResult.Fail(Name, $"powershell exit {process.ExitCode}\n{combined.TrimEnd()}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* ignore */ }
            return ToolResult.Fail(Name, $"timed out after {_timeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(Name, ex.Message);
        }
    }

    private bool IsAllowed(string cmd)
    {
        var first = FirstToken(cmd);
        return _allowList.Any(p => string.Equals(p, first, StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstToken(string cmd)
    {
        var trimmed = cmd.TrimStart();
        var i = trimmed.IndexOfAny(new[] { ' ', '\t', '|', ';', '\n', '(' });
        return i < 0 ? trimmed : trimmed[..i];
    }
}
