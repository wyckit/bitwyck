using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Lifecycle;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// <c>schedule_task|&lt;cron&gt;|&lt;jobId&gt;|&lt;prompt&gt;</c>
/// Registers a chrono-job that fires a CognitiveLoop turn on its schedule.
/// </summary>
public sealed class ScheduleTaskTool : ITool
{
    private readonly ChronoScheduler _scheduler;
    private readonly Func<SensoryEvent, CancellationToken, Task> _triggerLoop;

    public ScheduleTaskTool(ChronoScheduler scheduler, Func<SensoryEvent, CancellationToken, Task> triggerLoop)
    {
        _scheduler = scheduler;
        _triggerLoop = triggerLoop;
    }

    public string Name => "schedule_task";
    public string Description => "Register a recurring cognitive task with a cron expression.";
    public string ArgumentSchema => "cron|jobId|prompt";

    public Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (arguments.Count < 3) return Task.FromResult(ToolResult.Fail(Name, "expected: cron|jobId|prompt"));
        var cron = arguments[0];
        var jobId = arguments[1];
        var prompt = arguments[2];

        try
        {
            // Validate cron up front so we fail fast on malformed expressions.
            _ = CronExpression.Parse(cron);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(Name, $"invalid cron: {ex.Message}"));
        }

        var job = new ChronoLoopJob(jobId, cron, prompt, _triggerLoop);
        _scheduler.Register(job);
        return Task.FromResult(ToolResult.Ok(Name, $"scheduled {jobId} ({cron})"));
    }

    private sealed class ChronoLoopJob : IChronoJob
    {
        private readonly Func<SensoryEvent, CancellationToken, Task> _trigger;
        private readonly string _prompt;

        public ChronoLoopJob(string id, string cron, string prompt, Func<SensoryEvent, CancellationToken, Task> trigger)
        {
            JobId = id;
            CronExpression = cron;
            _prompt = prompt;
            _trigger = trigger;
        }

        public string JobId { get; }
        public string CronExpression { get; }

        public Task ExecuteAsync(CancellationToken ct)
        {
            var ev = SensoryEvent.FromText(_prompt, source: $"chrono:{JobId}") with
            {
                Channel = SensoryChannel.ChronoTrigger
            };
            return _trigger(ev, ct);
        }
    }
}
