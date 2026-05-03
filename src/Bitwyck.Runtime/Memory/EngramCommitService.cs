using System.Security.Cryptography;
using System.Text;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Memory;

/// <summary>
/// Writes one <see cref="CognitiveCycleResult"/> back into the engram store as
/// one or more entries:
/// <list type="bullet">
///   <item>An episodic "turn" entry summarising the full turn.</item>
///   <item>Per-tool-call entries for replay and audit.</item>
/// </list>
/// All entries are stored in <see cref="EngramOptions.DefaultNamespace"/>.
/// </summary>
public sealed class EngramCommitService
{
    private readonly IEngramMemoryStore _store;
    private readonly EngramOptions _options;

    public EngramCommitService(IEngramMemoryStore store, EngramOptions options)
    {
        _store = store;
        _options = options;
    }

    /// <summary>
    /// Commits all memory entries derived from <paramref name="result"/> to the store.
    /// </summary>
    public async Task CommitAsync(CognitiveCycleResult result, CancellationToken ct = default)
    {
        var ns = _options.DefaultNamespace;
        var stamp = DateTimeOffset.UtcNow;

        // ── Episodic turn entry ───────────────────────────────────────────────

        var episodicId = BuildEpisodicId(stamp, result.Trigger.Payload);
        var episodicText = BuildEpisodicText(result);
        var episodicMeta = BuildBaseMeta(result, stamp);

        var episodicEngram = new Engram(
            Id: episodicId,
            Namespace: ns,
            Text: episodicText,
            Category: "episodic",
            Lifecycle: EngramLifecycle.Stm,
            Score: 0.0,
            Timestamp: stamp,
            Metadata: episodicMeta);

        await _store.StoreAsync(episodicEngram, ct).ConfigureAwait(false);

        // ── Per-tool-call entries ─────────────────────────────────────────────

        for (int i = 0; i < result.ToolCalls.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var call = result.ToolCalls[i];
            var callResult = i < result.ToolResults.Count ? result.ToolResults[i] : null;

            var toolId = $"{episodicId}-tool-{i:D2}";
            var toolText = BuildToolCallText(call, callResult);
            var toolMeta = BuildToolMeta(result, call, callResult, stamp, i);

            var toolEngram = new Engram(
                Id: toolId,
                Namespace: ns,
                Text: toolText,
                Category: "tool-call",
                Lifecycle: EngramLifecycle.Stm,
                Score: 0.0,
                Timestamp: stamp,
                Metadata: toolMeta);

            await _store.StoreAsync(toolEngram, ct).ConfigureAwait(false);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildEpisodicId(DateTimeOffset stamp, string payload)
    {
        var datePart = stamp.ToString("yyyyMMdd-HHmmss");
        var hashPart = ShortHash(payload);
        return $"episodic-{datePart}-{hashPart}";
    }

    private static string ShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        // Take first 4 bytes as hex — gives 8 chars, low collision risk for audit IDs.
        return Convert.ToHexString(bytes[..4]).ToLowerInvariant();
    }

    private static string BuildEpisodicText(CognitiveCycleResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Turn: {result.CorrelationId}");
        sb.AppendLine($"Channel: {result.Trigger.Channel}");
        sb.AppendLine($"Trigger: {result.Trigger.Payload}");
        sb.AppendLine($"Route: {result.Route.SelectedTier} (confidence={result.Route.Confidence:F2})");
        sb.AppendLine($"Answer: {result.FinalAnswer}");
        if (result.DegradedMode && result.DegradedReason is not null)
            sb.AppendLine($"Degraded: {result.DegradedReason}");
        sb.AppendLine($"Tokens: prompt={result.TotalPromptTokens} completion={result.TotalCompletionTokens}");
        sb.AppendLine($"Duration: {result.Duration.TotalMilliseconds:F0}ms");
        return sb.ToString().TrimEnd();
    }

    private static string BuildToolCallText(ToolCall call, ToolResult? callResult)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Tool: {call.ToolName}");
        sb.AppendLine($"Args: {call.ArgsJoined}");
        if (callResult is not null)
        {
            sb.AppendLine($"Success: {callResult.Success}");
            if (callResult.Success)
                sb.AppendLine($"Output: {callResult.Output}");
            else
                sb.AppendLine($"Error: {callResult.Error}");
        }
        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyDictionary<string, string> BuildBaseMeta(
        CognitiveCycleResult result, DateTimeOffset stamp)
    {
        return new Dictionary<string, string>
        {
            ["CorrelationId"] = result.CorrelationId,
            ["TriggerId"] = result.Trigger.Id,
            ["Channel"] = result.Trigger.Channel.ToString(),
            ["Tier"] = result.Route.SelectedTier.ToString(),
            ["PromptTokens"] = result.TotalPromptTokens.ToString(),
            ["CompletionTokens"] = result.TotalCompletionTokens.ToString(),
            ["DurationMs"] = ((long)result.Duration.TotalMilliseconds).ToString(),
            ["DegradedMode"] = result.DegradedMode.ToString(),
            ["Timestamp"] = stamp.ToString("O"),
        };
    }

    private static IReadOnlyDictionary<string, string> BuildToolMeta(
        CognitiveCycleResult result,
        ToolCall call,
        ToolResult? callResult,
        DateTimeOffset stamp,
        int index)
    {
        return new Dictionary<string, string>
        {
            ["CorrelationId"] = result.CorrelationId,
            ["ToolName"] = call.ToolName,
            ["ToolIndex"] = index.ToString(),
            ["Success"] = (callResult?.Success ?? false).ToString(),
            ["Timestamp"] = stamp.ToString("O"),
        };
    }
}
