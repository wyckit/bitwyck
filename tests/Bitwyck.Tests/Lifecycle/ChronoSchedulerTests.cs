using Bitwyck.Core.Interfaces;
using Bitwyck.Runtime.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bitwyck.Tests.Lifecycle;

public class ChronoSchedulerTests
{
    // ── Snapshot returns registered jobs ──────────────────────────────────────

    [Fact]
    public void Snapshot_ReturnsRegisteredJobsWithCorrectId()
    {
        var scheduler = new ChronoScheduler(NullLogger<ChronoScheduler>.Instance);
        var job = new StubChronoJob("test-job-id", "0 9 * * *");

        scheduler.Register(job);
        var snapshot = scheduler.Snapshot();

        Assert.Single(snapshot);
        Assert.Equal("test-job-id", snapshot[0].JobId);
    }

    [Fact]
    public void Snapshot_NextRun_IsInTheFuture()
    {
        var scheduler = new ChronoScheduler(NullLogger<ChronoScheduler>.Instance);
        var job = new StubChronoJob("future-job", "* * * * *");

        scheduler.Register(job);
        var snapshot = scheduler.Snapshot();

        Assert.Single(snapshot);
        Assert.True(snapshot[0].NextRun > DateTimeOffset.UtcNow,
            "NextRun should be in the future for a wildcard expression");
    }

    // ── Multiple jobs in snapshot ─────────────────────────────────────────────

    [Fact]
    public void Snapshot_MultipleJobs_AllReturned()
    {
        var scheduler = new ChronoScheduler(NullLogger<ChronoScheduler>.Instance);
        scheduler.Register(new StubChronoJob("job-a", "0 9 * * *"));
        scheduler.Register(new StubChronoJob("job-b", "*/15 * * * *"));

        var snapshot = scheduler.Snapshot();

        Assert.Equal(2, snapshot.Count);
        var ids = snapshot.Select(s => s.JobId).ToHashSet();
        Assert.Contains("job-a", ids);
        Assert.Contains("job-b", ids);
    }

    // ── Unregister removes job ────────────────────────────────────────────────

    [Fact]
    public void Unregister_RemovesJobFromSnapshot()
    {
        var scheduler = new ChronoScheduler(NullLogger<ChronoScheduler>.Instance);
        var job = new StubChronoJob("remove-me", "* * * * *");
        scheduler.Register(job);

        scheduler.Unregister("remove-me");
        var snapshot = scheduler.Snapshot();

        Assert.Empty(snapshot);
    }

    // ── Re-registering same JobId replaces the previous ──────────────────────

    [Fact]
    public void Register_SameJobId_ReplacesExisting()
    {
        var scheduler = new ChronoScheduler(NullLogger<ChronoScheduler>.Instance);
        var first  = new StubChronoJob("same-id", "0 8 * * *");
        var second = new StubChronoJob("same-id", "0 10 * * *");

        scheduler.Register(first);
        scheduler.Register(second);

        // Only one entry expected
        Assert.Single(scheduler.Snapshot());
    }

    // ── Job execution via TickAsync (internal) ────────────────────────────────
    // The scheduler tick is hard-coded to 30 seconds and uses PeriodicTimer,
    // so we cannot reliably trigger it in a unit test without sleeping 30+ seconds.
    // Instead, we test the scheduler's public contract: register, snapshot, and
    // unregister work correctly. The firing logic is tested indirectly via
    // integration or manual inspection of the IHostedService lifecycle.

    // ── Helper ────────────────────────────────────────────────────────────────

    private sealed class StubChronoJob : IChronoJob
    {
        private int _executeCount;

        public StubChronoJob(string jobId, string cronExpression)
        {
            JobId = jobId;
            CronExpression = cronExpression;
        }

        public string JobId { get; }
        public string CronExpression { get; }
        public int ExecuteCount => _executeCount;

        public Task ExecuteAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _executeCount);
            return Task.CompletedTask;
        }
    }
}
