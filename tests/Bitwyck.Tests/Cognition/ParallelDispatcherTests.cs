using Bitwyck.Core.Models;
using Bitwyck.Runtime.Cognition;
using Bitwyck.Tests.Fakes;

namespace Bitwyck.Tests.Cognition;

public class ParallelDispatcherTests
{
    private static SystemBias DefaultBias => SystemBias.DeterministicTesting();

    // ── Empty input ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyInput_ReturnsEmptyResults()
    {
        var fake = new FakeBitNetClient();
        var dispatcher = new ParallelCognitiveDispatcher(fake);

        var results = await dispatcher.DispatchAsync(Array.Empty<string>(), DefaultBias);

        Assert.Empty(results);
    }

    // ── 3 tasks, each returns the default response ────────────────────────────

    [Fact]
    public async Task ThreeTasks_AllSucceed_ResultsHaveCorrectLabels()
    {
        var fake = new FakeBitNetClient { DefaultResponse = "done" };
        var dispatcher = new ParallelCognitiveDispatcher(fake);

        var subTasks = new[] { "task one", "task two", "task three" };
        var results = await dispatcher.DispatchAsync(subTasks, DefaultBias);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.All(results, r => Assert.Equal("done", r.Output));

        // Tasks preserved in the results
        var taskLabels = results.Select(r => r.Task).ToHashSet();
        Assert.Contains("task one", taskLabels);
        Assert.Contains("task two", taskLabels);
        Assert.Contains("task three", taskLabels);
    }

    // ── Aggregator output contains each result label ──────────────────────────

    [Fact]
    public async Task Aggregate_ContainsEachResultLabel()
    {
        var fake = new FakeBitNetClient { DefaultResponse = "answer" };
        var dispatcher = new ParallelCognitiveDispatcher(fake);

        var subTasks = new[] { "alpha task", "beta task" };
        var results = await dispatcher.DispatchAsync(subTasks, DefaultBias);
        var aggregated = ParallelCognitiveDispatcher.Aggregate(results);

        Assert.Contains("[1]", aggregated);
        Assert.Contains("[2]", aggregated);
        Assert.Contains("OK", aggregated);
        Assert.Contains("answer", aggregated);
    }

    // ── Concurrency cap: 4 tasks with maxConcurrency=2, all complete ──────────

    [Fact]
    public async Task ConcurrencyCap_AllFourTasksComplete()
    {
        var fake = new FakeBitNetClient { DefaultResponse = "result" };
        var dispatcher = new ParallelCognitiveDispatcher(fake, maxConcurrency: 2);

        var subTasks = new[] { "t1", "t2", "t3", "t4" };
        var results = await dispatcher.DispatchAsync(subTasks, DefaultBias);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded));
    }
}
