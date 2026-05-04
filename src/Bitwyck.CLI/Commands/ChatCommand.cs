using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Cognition;
using Microsoft.Extensions.DependencyInjection;

namespace Bitwyck.CLI.Commands;

/// <summary>
/// Persistent multi-turn chat with the full agent stack — IntentDispatcher
/// shortcut, then CognitiveLoop (LLM + tool interception) on miss. Engram
/// recall + commit happen on every turn (inside the loop), so the agent
/// remembers across messages within a session AND across sessions.
///
/// Built-in slash commands:
///   :quit   :q   :exit   leave the chat
///   :help            list commands
///   :clear           reset the conversational context (engram is preserved)
///   :forget          wipe engram (precedent + episodic)
///   :why             show what the dispatcher did on the last turn
///   :status          show turn count + engram entry count
/// </summary>
public sealed class ChatCommand
{
    private readonly IServiceProvider _services;
    public ChatCommand(IServiceProvider services) { _services = services; }

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var loop = _services.GetRequiredService<CognitiveLoop>();
        var dispatcher = _services.GetRequiredService<IntentDispatcher>();
        var registry = _services.GetRequiredService<IToolRegistry>();
        var store = _services.GetRequiredService<IEngramMemoryStore>();

        WriteColored(ConsoleColor.Cyan, "bitwyck chat");
        Console.WriteLine(" — type :help for commands, :quit to leave.");
        Console.WriteLine();

        var turnCount = 0;
        string? lastDispatchInfo = null;

        while (!ct.IsCancellationRequested)
        {
            WriteColored(ConsoleColor.Green, "you ▸ ");
            var line = Console.ReadLine();
            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            // Slash commands.
            if (line.StartsWith(':'))
            {
                var cmd = line.ToLowerInvariant();
                switch (cmd)
                {
                    case ":quit" or ":q" or ":exit":
                        WriteColored(ConsoleColor.DarkGray, $"({turnCount} turns)\n");
                        return 0;
                    case ":help":
                        Console.WriteLine("  :quit  :clear  :forget  :why  :status  :help");
                        continue;
                    case ":clear":
                        WriteColored(ConsoleColor.DarkGray, "(turn counter reset; engram preserved)\n");
                        turnCount = 0;
                        continue;
                    case ":forget":
                        WriteColored(ConsoleColor.Yellow, "wiping engram...\n");
                        await WipeEngramAsync(store, ct);
                        continue;
                    case ":why":
                        WriteColored(ConsoleColor.DarkGray, lastDispatchInfo ?? "(no turn yet)\n");
                        continue;
                    case ":status":
                        var count = await store.CountAsync(null, ct);
                        Console.WriteLine($"  turns={turnCount}  engram_entries={count}");
                        continue;
                    default:
                        WriteColored(ConsoleColor.Yellow, $"unknown command: {cmd}\n");
                        continue;
                }
            }

            turnCount++;

            // Pre-LLM shortcut.
            try
            {
                var dispatch = await dispatcher.TryDispatchAsync(line, ct);
                if (dispatch.IsHit && dispatch.Call is { } call &&
                    registry.TryGet(call.ToolName, out var tool) && tool is not null)
                {
                    WriteColored(ConsoleColor.Magenta, $"⚡ ");
                    Console.WriteLine($"{call.ToolName}({string.Join(", ", call.Arguments)})  [{dispatch.Source} {dispatch.Confidence:F2}]");

                    ToolResult tr;
                    try { tr = await tool.ExecuteAsync(call.Arguments, ct); }
                    catch (Exception ex) { tr = ToolResult.Fail(call.ToolName, ex.Message); }

                    if (tr.Success)
                    {
                        Console.WriteLine(tr.Output);
                        await dispatcher.RecordSuccessAsync(line, call, ct);
                        lastDispatchInfo = $"shortcut hit: {dispatch.Source} (conf={dispatch.Confidence:F2}) -> {call.ToolName}\n";
                    }
                    else
                    {
                        WriteColored(ConsoleColor.Yellow, $"shortcut failed: {tr.Error}\n");
                        WriteColored(ConsoleColor.DarkGray, "→ falling through to LLM...\n");
                        await RunLlmTurnAsync(loop, line, ct);
                        lastDispatchInfo = $"shortcut failed -> LLM fallback. Error: {tr.Error}\n";
                    }
                    Console.WriteLine();
                    continue;
                }

                // No shortcut — LLM turn.
                lastDispatchInfo = "dispatcher miss — LLM handled the turn.\n";
                await RunLlmTurnAsync(loop, line, ct);
                Console.WriteLine();
            }
            catch (OperationCanceledException) { return 0; }
            catch (Exception ex)
            {
                WriteColored(ConsoleColor.Red, $"[error: {ex.Message}]\n");
            }
        }
        return 0;
    }

    private static async Task RunLlmTurnAsync(CognitiveLoop loop, string text, CancellationToken ct)
    {
        var ev = SensoryEvent.FromText(text, source: "chat");
        var result = await loop.RunAsync(ev, ct);

        WriteColored(ConsoleColor.White, "agent ▸ ");
        Console.WriteLine(result.FinalAnswer.Trim());

        if (result.ToolCalls.Count > 0)
            WriteColored(ConsoleColor.DarkGray, $"  (tools: {string.Join(", ", result.ToolCalls.Select(c => c.ToolName))})\n");
        if (result.DegradedMode)
            WriteColored(ConsoleColor.Yellow, $"  ⚠ degraded: {result.DegradedReason}\n");
    }

    private static async Task WipeEngramAsync(IEngramMemoryStore store, CancellationToken ct)
    {
        // The store has no bulk delete — query everything in our two namespaces and delete one by one.
        foreach (var ns in new[] { "bitwyck-episodic", "bitwyck-precedent" })
        {
            var entries = await store.SearchAsync(
                new EngramQuery(string.Empty, ns, K: 10000, Hybrid: false, MinScore: 0), ct);
            foreach (var e in entries)
                await store.DeleteAsync(e.Id, ns, ct);
        }
    }

    private static void WriteColored(ConsoleColor color, string text)
    {
        var prev = Console.ForegroundColor;
        try { Console.ForegroundColor = color; Console.Write(text); }
        finally { Console.ForegroundColor = prev; }
    }
}
