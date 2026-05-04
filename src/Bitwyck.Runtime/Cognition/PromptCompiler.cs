using System.Text;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Core.Utilities;

namespace Bitwyck.Runtime.Cognition;

/// <summary>
/// Lower-level prompt assembly. Given a CognitiveContext + tool manifest,
/// produces the final list of <see cref="InferenceMessage"/>s the BitNet
/// client will see. Token-budget-aware via <see cref="TokenBudget"/>.
///
/// Sister to <c>Memory/ContextCompiler</c> — that one focuses on memory
/// pruning; this one wires in tool instructions and routing-aware framing.
/// </summary>
public sealed class PromptCompiler
{
    private readonly IToolRegistry _registry;

    public PromptCompiler(IToolRegistry registry) { _registry = registry; }

    public IReadOnlyList<InferenceMessage> Compile(CognitiveContext context, RouteDecision route)
    {
        // Tier-aware compaction. The 7B and 10B BitNet kernels in this build
        // crash on prompts above ~600 chars (separate bug from 1B's stack
        // overflow — bumping the binary stack doesn't fix it). When routing
        // to those tiers, strip the tool manifest and recall context entirely
        // and emit a one-line persona + the trigger. Quality is fine for one-
        // shot chat at that size.
        var smallEnvelope = route.SelectedTier is ModelTier.Deliberate_7B or ModelTier.DeepReason_10B;

        var system = smallEnvelope
            ? BuildMinimalSystemMessage(context)
            : BuildSystemMessage(context, route);
        var user = smallEnvelope
            ? context.Trigger.Payload
            : BuildUserMessage(context);

        var messages = new List<InferenceMessage>(2 + context.ChatHistory.Count) {
            new(MessageRole.System, system),
        };
        // Skip chat history for small-envelope tiers — it accumulates and crashes them.
        if (!smallEnvelope && context.ChatHistory.Count > 0)
            messages.AddRange(context.ChatHistory);
        messages.Add(new InferenceMessage(MessageRole.User, user));

        return messages;
    }

    private static string BuildMinimalSystemMessage(CognitiveContext ctx) => ctx.Bias.Persona;

    private string BuildSystemMessage(CognitiveContext ctx, RouteDecision route)
    {
        // Compact system prompt. BitNet 1.58-bit Instruct models are quite sensitive
        // to long system prompts (they tend to emit EOS immediately if the prompt
        // exceeds a couple hundred tokens). We keep persona one-line and only show
        // the tool manifest when the previous turn had recall hits or the trigger
        // looks like it might need tools (heuristic: payload length > 30 chars).
        var sb = new StringBuilder();
        sb.Append(ctx.Bias.Persona);
        var needsTools = ctx.RecalledEngrams.Count > 0
                         || ctx.Trigger.Payload.Length > 30
                         || ctx.Trigger.Payload.Contains("file", StringComparison.OrdinalIgnoreCase)
                         || ctx.Trigger.Payload.Contains("list", StringComparison.OrdinalIgnoreCase)
                         || ctx.Trigger.Payload.Contains("read", StringComparison.OrdinalIgnoreCase);
        if (needsTools)
        {
            var manifest = _registry.ToPromptManifest();
            if (!string.IsNullOrWhiteSpace(manifest) && manifest != "(no tools)")
            {
                // <call>...</call> tags don't collide with ChatML's <|tag|> tokens
                // since the interceptor parses literal <call>...</call>.
                sb.Append("\nTools (invoke as <call>name|args</call>): ").Append(manifest);
            }
        }
        return sb.ToString();
    }

    private string BuildUserMessage(CognitiveContext ctx)
    {
        // For clean conversational replies we put recall context as a leading note
        // when present, but otherwise emit just the trigger payload — leading
        // headers like "# Trigger" cause the model to echo them back into its reply.
        if (ctx.RecalledEngrams.Count == 0) return ctx.Trigger.Payload;

        var sb = new StringBuilder();
        sb.AppendLine("Relevant prior context:");
        foreach (var e in ctx.RecalledEngrams)
        {
            sb.Append("- (score=").Append(e.Score.ToString("F2")).Append(") ");
            sb.AppendLine(TokenBudget.Truncate(e.Text, 120));
        }
        sb.AppendLine();
        sb.Append(ctx.Trigger.Payload);
        return sb.ToString();
    }
}
