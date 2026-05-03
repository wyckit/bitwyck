using System.Collections.Concurrent;
using Bitwyck.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bitwyck.Runtime.Lifecycle;

/// <summary>
/// Cron-job scheduler hosted service.  Uses a 30-second PeriodicTimer so cron
/// resolution is ~1 minute.  Each registered <see cref="IChronoJob"/> is fired
/// at most once per calendar minute, even if the timer fires twice within that
/// minute.  All job executions are isolated via per-job semaphores so
/// concurrent runs of the same job are prevented.
/// </summary>
public sealed class ChronoScheduler : IHostedService, IDisposable
{
    private readonly ILogger<ChronoScheduler> _logger;

    // Registered jobs, keyed by JobId
    private readonly ConcurrentDictionary<string, IChronoJob> _jobs = new(StringComparer.Ordinal);

    // Semaphores prevent concurrent runs of the same job
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new(StringComparer.Ordinal);

    // Guards the "at most once per minute" invariant.
    // Value is the UTC minute boundary at which the job last fired.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFired = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public ChronoScheduler(ILogger<ChronoScheduler> logger)
    {
        _logger = logger;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunLoopAsync(_cts.Token);
        _logger.LogInformation("ChronoScheduler started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { /* graceful */ }
        }

        _logger.LogInformation("ChronoScheduler stopped.");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Register a job.  Idempotent — registering the same JobId twice replaces the first.</summary>
    public void Register(IChronoJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Eagerly validate the cron expression so caller gets an immediate error.
        _ = CronExpression.Parse(job.CronExpression);

        _jobs[job.JobId] = job;
        _semaphores.TryAdd(job.JobId, new SemaphoreSlim(1, 1));
        _logger.LogInformation("ChronoJob {JobId} registered with expression \"{CronExpr}\".",
            job.JobId, job.CronExpression);
    }

    /// <summary>Remove a registered job by id.  No-op if the id is unknown.</summary>
    public void Unregister(string jobId)
    {
        _jobs.TryRemove(jobId, out _);
        _lastFired.TryRemove(jobId, out _);

        if (_semaphores.TryRemove(jobId, out var sem))
            sem.Dispose();

        _logger.LogInformation("ChronoJob {JobId} unregistered.", jobId);
    }

    /// <summary>Returns a diagnostic snapshot of all registered jobs and their next scheduled run.</summary>
    public IReadOnlyList<(string JobId, DateTimeOffset NextRun)> Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var results = new List<(string, DateTimeOffset)>(_jobs.Count);

        foreach (var (id, job) in _jobs)
        {
            try
            {
                var next = CronExpression.Parse(job.CronExpression).Next(now);
                results.Add((id, next));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ChronoJob {JobId} has an unparseable cron expression.", id);
                results.Add((id, DateTimeOffset.MaxValue));
            }
        }

        return results.AsReadOnly();
    }

    // ── Core loop ─────────────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTimeOffset.UtcNow;
                TickAsync(now, ct);  // fire-and-forget; each job is individually guarded
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — expected
        }
    }

    private void TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Truncate to the current minute boundary for the "already fired" guard
        var minuteBoundary = new DateTimeOffset(
            now.Year, now.Month, now.Day,
            now.Hour, now.Minute, 0, now.Offset);

        foreach (var (id, job) in _jobs)
        {
            CronExpression expr;
            try
            {
                expr = CronExpression.Parse(job.CronExpression);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex,
                    "ChronoJob {JobId} has invalid cron expression \"{CronExpr}\" — skipping.",
                    id, job.CronExpression);
                continue;
            }

            if (!expr.Matches(now))
                continue;

            // "At most once per minute" guard
            if (_lastFired.TryGetValue(id, out var lastFiredMinute)
                && lastFiredMinute == minuteBoundary)
            {
                _logger.LogDebug(
                    "ChronoJob {JobId} already fired at {Minute} — suppressing duplicate tick.",
                    id, minuteBoundary);
                continue;
            }

            _lastFired[id] = minuteBoundary;

            var sem = _semaphores.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

            // Run on thread-pool; capture variables for closure
            var capturedJob = job;
            var capturedId  = id;
            var capturedNow = now;

            _ = Task.Run(async () =>
            {
                if (!await sem.WaitAsync(0, ct))
                {
                    _logger.LogWarning(
                        "ChronoJob {JobId} is still running from a previous invocation — skipping.",
                        capturedId);
                    return;
                }

                try
                {
                    _logger.LogInformation("ChronoJob {JobId} fired at {Time}.", capturedId, capturedNow);
                    await capturedJob.ExecuteAsync(ct);
                    _logger.LogInformation("ChronoJob {JobId} completed successfully.", capturedId);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("ChronoJob {JobId} was cancelled.", capturedId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ChronoJob {JobId} threw an unhandled exception.", capturedId);
                }
                finally
                {
                    sem.Release();
                }
            }, ct);
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts?.Dispose();

        foreach (var sem in _semaphores.Values)
            sem.Dispose();
    }
}
