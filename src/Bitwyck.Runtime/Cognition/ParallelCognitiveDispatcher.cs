using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Cognition;

public sealed record SubAgentResult(string Task, string Output, bool Succeeded, TimeSpan Duration);

/// <summary>
/// Fans out a list of sub-tasks to the Reflex_1B tier in parallel, with a
/// concurrency cap, and aggregates their outputs.
///
/// Used by the SpawnAgentTool: when the master 10B reasoner emits
/// <![CDATA[<call>spawn_agent|task1|task2|task3</call>]]>, the harness
/// dispatches each task to a 1B sub-thread and joins the results.
/// </summary>
public sealed class ParallelCognitiveDispatcher
{
    private readonly IBitNetInferenceClient _inference;
    private readonly int _maxConcurrency;
    private readonly int _maxTokensPerSubTask;

    public ParallelCognitiveDispatcher(
        IBitNetInferenceClient inference,
        int maxConcurrency = 4,
        int maxTokensPerSubTask = 256)
    {
        _inference = inference;
        _maxConcurrency = Math.Max(1, maxConcurrency);
        _maxTokensPerSubTask = maxTokensPerSubTask;
    }

    public async Task<IReadOnlyList<SubAgentResult>> DispatchAsync(
        IReadOnlyList<string> subTasks,
        SystemBias bias,
        CancellationToken ct = default)
    {
        if (subTasks.Count == 0) return Array.Empty<SubAgentResult>();

        using var sem = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        var tasks = subTasks.Select(t => RunOneAsync(t, bias, sem, ct)).ToArray();
        return await Task.WhenAll(tasks);
    }

    private async Task<SubAgentResult> RunOneAsync(
        string task, SystemBias bias, SemaphoreSlim sem, CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var req = new InferenceRequest(
                Tier: ModelTier.Reflex_1B,
                Messages: new[]
                {
                    new InferenceMessage(MessageRole.System,
                        $"{bias.Persona}\nYou are a parallel sub-agent. Answer the user's task tersely; return only the answer body, no preamble."),
                    new InferenceMessage(MessageRole.User, task),
                },
                MaxTokens: _maxTokensPerSubTask,
                Temperature: bias.Temperature,
                TopP: bias.TopP,
                Seed: bias.Seed);

            var resp = await _inference.CompleteAsync(req, ct);
            return new SubAgentResult(task, resp.Content, true, sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new SubAgentResult(task, $"[sub-agent failed: {ex.Message}]", false, sw.Elapsed);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Aggregates sub-agent results into a single observation block.</summary>
    public static string Aggregate(IReadOnlyList<SubAgentResult> results)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.Append('[').Append(i + 1).Append("] ");
            sb.Append(r.Succeeded ? "OK" : "FAIL").Append(" (").Append(r.Duration.TotalMilliseconds.ToString("F0")).Append("ms): ");
            sb.AppendLine(r.Output);
        }
        return sb.ToString();
    }
}
