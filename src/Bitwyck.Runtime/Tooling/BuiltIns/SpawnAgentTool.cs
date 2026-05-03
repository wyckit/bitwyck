using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Cognition;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// <c>spawn_agent|task1|task2|...|taskN</c>
/// Fans out each task to a Reflex_1B sub-agent in parallel and returns the aggregated outputs.
/// </summary>
public sealed class SpawnAgentTool : ITool
{
    private readonly ParallelCognitiveDispatcher _dispatcher;
    private readonly Func<SystemBias> _biasProvider;

    public SpawnAgentTool(ParallelCognitiveDispatcher dispatcher, Func<SystemBias>? biasProvider = null)
    {
        _dispatcher = dispatcher;
        _biasProvider = biasProvider ?? (() => SystemBias.Default());
    }

    public string Name => "spawn_agent";
    public string Description => "Run sub-tasks in parallel via the fast 1B tier and return joined outputs.";
    public string ArgumentSchema => "task1|task2|...";

    public async Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var tasks = arguments.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
        if (tasks.Length == 0) return ToolResult.Fail(Name, "no tasks provided");

        try
        {
            var results = await _dispatcher.DispatchAsync(tasks, _biasProvider(), ct);
            return ToolResult.Ok(Name, ParallelCognitiveDispatcher.Aggregate(results));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(Name, ex.Message);
        }
    }
}
