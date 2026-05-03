using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Cognition;
using Microsoft.Extensions.DependencyInjection;

namespace Bitwyck.CLI.Commands;

/// <summary>
/// Iterative agent loop: takes a goal and runs the CognitiveLoop repeatedly,
/// feeding each turn's tool observations forward as additional context, until
/// the model produces a final answer with no further tool calls (or hits the
/// iteration cap).
/// </summary>
public sealed class AgentCommand
{
    private readonly IServiceProvider _services;
    private const int DefaultMaxIterations = 6;

    public AgentCommand(IServiceProvider services) { _services = services; }

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        // Parse: bitwyck agent [--max N] <goal...>
        var maxIterations = DefaultMaxIterations;
        var goalParts = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--max" or "-n" && i + 1 < args.Length && int.TryParse(args[++i], out var m))
                maxIterations = Math.Clamp(m, 1, 25);
            else
                goalParts.Add(args[i]);
        }

        var goal = string.Join(' ', goalParts).Trim();
        if (string.IsNullOrEmpty(goal))
        {
            Console.Error.WriteLine("usage: bitwyck agent [--max N] <goal>");
            Console.Error.WriteLine("example: bitwyck agent \"list the files in src and tell me what's there\"");
            return 2;
        }

        var loop = _services.GetRequiredService<CognitiveLoop>();
        var dispatcher = _services.GetRequiredService<IntentDispatcher>();
        var registry = _services.GetRequiredService<IToolRegistry>();

        Console.WriteLine();
        WriteColored(ConsoleColor.Cyan, "▶ goal:  ");
        Console.WriteLine(goal);
        Console.WriteLine();

        var transcript = new System.Text.StringBuilder();
        transcript.AppendLine($"Goal: {goal}");
        var lastTrigger = goal;

        for (var iter = 1; iter <= maxIterations; iter++)
        {
            WriteColored(ConsoleColor.DarkGray, $"── iteration {iter}/{maxIterations} ──\n");

            // Pre-LLM shortcut: deterministic intent dispatch. Avoids the LLM
            // entirely when the trigger maps cleanly to a tool.
            var dispatch = await dispatcher.TryDispatchAsync(lastTrigger, ct);
            if (dispatch.IsHit && dispatch.Call is { } directCall && registry.TryGet(directCall.ToolName, out var directTool) && directTool is not null)
            {
                WriteColored(ConsoleColor.Magenta, $"⚡ shortcut [{dispatch.Source}, conf={dispatch.Confidence:F2}]: ");
                Console.WriteLine($"{directCall.ToolName}({string.Join(", ", directCall.Arguments)})");
                ToolResult tr;
                try { tr = await directTool.ExecuteAsync(directCall.Arguments, ct); }
                catch (Exception ex) { tr = ToolResult.Fail(directCall.ToolName, ex.Message); }

                Console.WriteLine(tr.Success ? tr.Output : $"[FAIL] {tr.Error}");
                Console.WriteLine();

                if (tr.Success)
                {
                    await dispatcher.RecordSuccessAsync(lastTrigger, directCall, ct);
                    WriteColored(ConsoleColor.Green, "✔ agent finished (shortcut handled the goal).\n");
                    return 0;
                }

                // Shortcut failed (e.g. allow-list rejection, missing file) —
                // fall through to the LLM with the failure observation in context.
                WriteColored(ConsoleColor.Yellow, "⚠ shortcut failed — escalating to LLM.\n");
                transcript.AppendLine();
                transcript.AppendLine($"--- shortcut failed turn {iter} ---");
                transcript.AppendLine(tr.ToObservation());
                lastTrigger = $"the shortcut failed: {tr.Error}. Continue toward the goal: {goal}";
            }

            // Feed forward: each iteration's prompt is the goal plus all prior tool observations.
            var ev = SensoryEvent.FromText(transcript.ToString(), source: $"agent:iter{iter}");
            var result = await loop.RunAsync(ev, ct);

            // Show the assistant's reasoning text (with <call>/<observation> blocks
            // inlined). The interceptor already substituted observations for calls.
            WriteColored(ConsoleColor.White, result.FinalAnswer.Trim());
            Console.WriteLine();

            if (result.ToolCalls.Count > 0)
            {
                Console.WriteLine();
                WriteColored(ConsoleColor.DarkGray, "  tools used: ");
                Console.WriteLine(string.Join(", ", result.ToolCalls.Select(c =>
                    $"{c.ToolName}({c.Arguments.Count})")));
            }
            Console.WriteLine();

            if (result.DegradedMode)
            {
                WriteColored(ConsoleColor.Yellow, $"⚠ degraded: {result.DegradedReason}\n");
                return 1;
            }

            // Append this iteration's output to the running transcript so the
            // next turn sees what happened.
            transcript.AppendLine();
            transcript.AppendLine($"--- assistant turn {iter} ---");
            transcript.AppendLine(result.FinalAnswer.Trim());

            // Termination: if no tools were called this turn, the agent has
            // either finished or has nothing more to do.
            if (result.ToolCalls.Count == 0)
            {
                WriteColored(ConsoleColor.Green, "✔ agent finished (no further tool calls).\n");
                return 0;
            }
        }

        WriteColored(ConsoleColor.Yellow, $"⚠ reached max iterations ({maxIterations}) — stopping.\n");
        return 0;
    }

    private static void WriteColored(ConsoleColor color, string text)
    {
        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.Write(text);
        }
        finally
        {
            Console.ForegroundColor = prev;
        }
    }
}
