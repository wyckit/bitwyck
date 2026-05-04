using System.Diagnostics;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Core.Utilities;
using Bitwyck.Runtime.Lifecycle;

namespace Bitwyck.Runtime.Cognition;

/// <summary>
/// Master orchestrator. One <see cref="RunAsync"/> call drives one cognitive
/// turn end-to-end: sense → recall → route → execute → intercept tools →
/// (optionally synthesize a new skill) → commit.
///
/// All subsystems are injected via delegates / interfaces so the loop is
/// testable with a <c>FakeBitNetClient</c> + in-memory engram + minimal
/// tool registry.
/// </summary>
public sealed class CognitiveLoop
{
    public delegate Task<IReadOnlyList<Engram>> RecallFn(SensoryEvent ev, UserIdentityState ident, CancellationToken ct);
    public delegate Task<InterceptionOutcome> InterceptFn(
        IAsyncEnumerable<InferenceTokenChunk> stream,
        Func<ToolCall, CancellationToken, Task<ToolResult>> handler,
        CancellationToken ct);
    public delegate Task CommitFn(CognitiveCycleResult result, CancellationToken ct);

    private readonly IBitNetInferenceClient _inference;
    private readonly ICognitiveRouter _router;
    private readonly ISystemBiasProvider _bias;
    private readonly IToolRegistry _tools;
    private readonly PromptCompiler _promptCompiler;
    private readonly RecallFn _recall;
    private readonly InterceptFn _intercept;
    private readonly CommitFn _commit;
    private readonly IIdentityStore _identityStore;

    public CognitiveLoop(
        IBitNetInferenceClient inference,
        ICognitiveRouter router,
        ISystemBiasProvider bias,
        IToolRegistry tools,
        PromptCompiler promptCompiler,
        RecallFn recall,
        InterceptFn intercept,
        CommitFn commit,
        IIdentityStore identityStore)
    {
        _inference = inference;
        _router = router;
        _bias = bias;
        _tools = tools;
        _promptCompiler = promptCompiler;
        _recall = recall;
        _intercept = intercept;
        _commit = commit;
        _identityStore = identityStore;
    }

    public Task<CognitiveCycleResult> RunAsync(SensoryEvent trigger, CancellationToken ct = default)
        => RunAsync(trigger, Array.Empty<InferenceMessage>(), ct);

    public async Task<CognitiveCycleResult> RunAsync(
        SensoryEvent trigger,
        IReadOnlyList<InferenceMessage> chatHistory,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var correlationId = Guid.NewGuid().ToString("N")[..12];

        var identity = await _identityStore.LoadAsync(ct);
        var recalled = await _recall(trigger, identity, ct);
        var route = _router.Route(trigger, recalled, identity);
        var systemBias = _bias.GetBias(trigger, route);

        var degraded = false;
        string? degradedReason = null;

        // Attempt the configured tier; fall back through the chain on failure.
        var toTry = (IReadOnlyList<ModelTier>)
            (route.FallbackChain is { Count: > 0 } ? route.FallbackChain : new[] { route.SelectedTier });

        InterceptionOutcome? intercepted = null;
        InferenceResponse? rawResp = null;

        foreach (var tier in toTry)
        {
            try
            {
                if (!await _inference.IsAvailableAsync(tier, ct))
                {
                    degraded = true;
                    degradedReason = $"tier {tier} unavailable; cascading";
                    continue;
                }

                var ctx = new CognitiveContext(
                    Trigger: trigger,
                    Identity: identity,
                    RecalledEngrams: recalled,
                    ChatHistory: chatHistory,
                    Bias: systemBias,
                    CorrelationId: correlationId);

                var messages = _promptCompiler.Compile(ctx, route with { SelectedTier = tier });

                var req = new InferenceRequest(
                    Tier: tier,
                    Messages: messages,
                    MaxTokens: 1024,
                    Temperature: systemBias.Temperature,
                    TopP: systemBias.TopP,
                    Seed: systemBias.Seed);

                var stream = _inference.StreamAsync(req, ct);
                intercepted = await _intercept(stream,
                    async (call, ictCt) =>
                    {
                        if (!_tools.TryGet(call.ToolName, out var tool) || tool is null)
                            return ToolResult.Fail(call.ToolName, $"unknown tool: {call.ToolName}");
                        try
                        {
                            return await tool.ExecuteAsync(call.Arguments, ictCt);
                        }
                        catch (Exception ex)
                        {
                            return ToolResult.Fail(call.ToolName, ex.Message);
                        }
                    },
                    ct);

                // Approx token counts (we don't have true counts from streaming).
                var promptText = string.Join("\n", messages.Select(m => m.Content));
                rawResp = new InferenceResponse(
                    Content: intercepted.AssembledOutput,
                    PromptTokens: TokenBudget.Estimate(promptText),
                    CompletionTokens: TokenBudget.Estimate(intercepted.AssembledOutput),
                    Model: tier.ToString(),
                    Duration: sw.Elapsed);
                break;
            }
            catch (Exception ex)
            {
                degraded = true;
                degradedReason = $"tier {tier} threw: {ex.Message}; cascading";
            }
        }

        if (intercepted is null || rawResp is null)
        {
            sw.Stop();
            var failResult = new CognitiveCycleResult(
                CorrelationId: correlationId,
                Trigger: trigger,
                Route: route,
                FinalAnswer: $"[bitwyck: all tiers in cascade failed. Reason: {degradedReason ?? "unknown"}]",
                ToolCalls: Array.Empty<ToolCall>(),
                ToolResults: Array.Empty<ToolResult>(),
                TotalPromptTokens: 0,
                TotalCompletionTokens: 0,
                Duration: sw.Elapsed,
                DegradedMode: true,
                DegradedReason: degradedReason);

            try { await _commit(failResult, ct); } catch { /* commit best-effort */ }
            return failResult;
        }

        sw.Stop();
        var result = new CognitiveCycleResult(
            CorrelationId: correlationId,
            Trigger: trigger,
            Route: route,
            FinalAnswer: intercepted.AssembledOutput,
            ToolCalls: intercepted.ToolCalls,
            ToolResults: intercepted.ToolResults,
            TotalPromptTokens: rawResp.PromptTokens,
            TotalCompletionTokens: rawResp.CompletionTokens,
            Duration: sw.Elapsed,
            DegradedMode: degraded,
            DegradedReason: degraded ? degradedReason : null);

        // Don't pollute engram with degraded / empty / stock-refusal turns —
        // they create feedback loops where the next turn recalls the refusal
        // and the model just repeats it.
        if (!result.DegradedMode
            && !string.IsNullOrWhiteSpace(result.FinalAnswer)
            && !RefusalHeuristic.LooksLikeRefusal(result.FinalAnswer))
            await _commit(result, ct);

        return result;
    }
}

/// <summary>Result from the <see cref="CognitiveLoop.InterceptFn"/> callback.</summary>
public sealed record InterceptionOutcome(
    string AssembledOutput,
    IReadOnlyList<ToolCall> ToolCalls,
    IReadOnlyList<ToolResult> ToolResults
);

internal static class RefusalHeuristic
{
    private static readonly string[] Phrases =
    {
        "i'm sorry, but i cannot",
        "i'm sorry, but i can't",
        "i cannot provide an answer",
        "i cannot assist with that",
        "i can't assist with that",
        "i am unable to",
        "i'm not able to",
        "as an ai language model",
        "i don't have the ability",
    };

    public static bool LooksLikeRefusal(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.TrimStart().ToLowerInvariant();
        // Only treat as refusal if it BEGINS with one of these phrases (so
        // factual answers that quote a refusal in the middle aren't dropped).
        foreach (var p in Phrases)
            if (lower.StartsWith(p)) return true;
        return false;
    }
}
