using System.Collections.Concurrent;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Tooling;

/// <summary>
/// Wraps an <see cref="ITool"/> to capture timing and outcome, storing
/// entries in an in-memory ring buffer (capacity 256) for diagnostics.
/// </summary>
public sealed class ToolExecutionLogger
{
    private const int RingCapacity = 256;

    public sealed record LogEntry(
        string ToolName,
        IReadOnlyList<string> Arguments,
        bool Success,
        TimeSpan Duration,
        string? Error,
        DateTimeOffset Timestamp);

    private readonly LogEntry?[] _ring = new LogEntry?[RingCapacity];
    private int _head;  // points to next write slot (mod RingCapacity)
    private int _count; // total entries ever written (unbounded, for position tracking)
    private readonly object _lock = new();

    /// <summary>
    /// Executes <paramref name="tool"/> and records a <see cref="LogEntry"/>
    /// with elapsed time regardless of success or failure.
    /// </summary>
    public async Task<ToolResult> ExecuteAndLogAsync(
        ITool tool,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var started = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(arguments, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            result = ToolResult.Fail(tool.Name, "Execution cancelled.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            result = ToolResult.Fail(tool.Name, $"Unhandled exception: {ex.Message}", sw.Elapsed);
        }
        finally
        {
            sw.Stop();
        }

        var entry = new LogEntry(
            ToolName: tool.Name,
            Arguments: arguments,
            Success: result.Success,
            Duration: result.Duration ?? sw.Elapsed,
            Error: result.Error,
            Timestamp: started);

        lock (_lock)
        {
            _ring[_head % RingCapacity] = entry;
            _head = (_head + 1) % RingCapacity;
            _count++;
        }

        return result;
    }

    /// <summary>
    /// Returns up to the last <see cref="RingCapacity"/> log entries in
    /// chronological order (oldest first).
    /// </summary>
    public IReadOnlyList<LogEntry> GetRecent()
    {
        lock (_lock)
        {
            var total = Math.Min(_count, RingCapacity);
            var result = new List<LogEntry>(total);

            // If we've filled less than one ring, start from 0; otherwise from _head (oldest slot).
            int startSlot = (_count < RingCapacity) ? 0 : _head;

            for (int i = 0; i < total; i++)
            {
                var entry = _ring[(startSlot + i) % RingCapacity];
                if (entry is not null)
                    result.Add(entry);
            }

            return result.AsReadOnly();
        }
    }

    /// <summary>Total number of tool executions logged (may exceed ring capacity).</summary>
    public int TotalExecutions { get { lock (_lock) return _count; } }
}
