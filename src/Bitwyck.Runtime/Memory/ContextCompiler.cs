using System.Text;
using Bitwyck.Core.Models;
using Bitwyck.Core.Utilities;

namespace Bitwyck.Runtime.Memory;

/// <summary>
/// Assembles the final <see cref="IReadOnlyList{T}"/> of <see cref="InferenceMessage"/>
/// from a <see cref="CognitiveContext"/>, respecting a token budget.
///
/// Ordering contract:
/// <list type="number">
///   <item>System message: persona + tool manifest + user identity block.</item>
///   <item>User message: recalled engrams (markdown block) + trigger payload.</item>
///   <item>Chat history messages (oldest first), truncated if needed.</item>
/// </list>
///
/// Budget allocation:
/// <list type="bullet">
///   <item>System + engrams section: up to 60 % of <see cref="CognitiveContext.TokenBudget"/>.</item>
///   <item>Trigger + chat history: remaining 40 %.</item>
/// </list>
/// Overflow truncation order: oldest chat messages first, then lowest-score engrams.
/// </summary>
public sealed class ContextCompiler
{
    /// <summary>
    /// Compiles the inference message list from the provided cognitive context.
    /// </summary>
    /// <param name="context">The per-turn cognitive state.</param>
    /// <param name="toolManifest">Pre-rendered string describing available tools.</param>
    /// <returns>Ordered list of messages ready for an inference request.</returns>
    public IReadOnlyList<InferenceMessage> Compile(CognitiveContext context, string toolManifest)
    {
        int totalBudget = context.TokenBudget > 0 ? context.TokenBudget : 4096;
        int systemBudget = (int)(totalBudget * 0.6);
        int restBudget = totalBudget - systemBudget;

        // ── 1. System message ─────────────────────────────────────────────────

        var systemText = BuildSystemText(context.Identity, toolManifest, systemBudget,
            out int systemTokensUsed);

        // ── 2. Engrams block ──────────────────────────────────────────────────
        // The engrams share the system budget; whatever remains after the system
        // text goes to the engram block.

        int engramBudget = Math.Max(0, systemBudget - systemTokensUsed);
        var engramBlock = BuildEngramBlock(context.RecalledEngrams, engramBudget);

        // ── 3. User message: engrams + trigger ────────────────────────────────

        var triggerBudget = restBudget;
        var userText = BuildUserText(engramBlock, context.Trigger.Payload, triggerBudget);

        // ── 4. Chat history ───────────────────────────────────────────────────

        int historyBudget = Math.Max(0, restBudget - TokenBudget.Estimate(userText));
        var history = TrimHistory(context.ChatHistory, historyBudget);

        // ── Assemble ──────────────────────────────────────────────────────────

        var messages = new List<InferenceMessage>(2 + history.Count)
        {
            new(MessageRole.System, systemText),
            new(MessageRole.User, userText),
        };
        messages.AddRange(history);

        return messages;
    }

    // ── Builders ──────────────────────────────────────────────────────────────

    private static string BuildSystemText(
        UserIdentityState identity,
        string toolManifest,
        int budget,
        out int tokensUsed)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(toolManifest))
        {
            sb.AppendLine(toolManifest);
            sb.AppendLine();
        }

        var identityBlock = identity.ToPromptBlock();
        sb.Append(identityBlock);

        var raw = sb.ToString();
        var estimated = TokenBudget.Estimate(raw);

        if (estimated <= budget)
        {
            tokensUsed = estimated;
            return raw;
        }

        // Truncate from the end (identity block detail) to fit.
        var truncated = TokenBudget.Truncate(raw, budget);
        tokensUsed = TokenBudget.Estimate(truncated);
        return truncated;
    }

    private static string BuildEngramBlock(
        IReadOnlyList<Engram> engrams,
        int budget)
    {
        if (engrams.Count == 0 || budget <= 0)
            return string.Empty;

        // Sort highest-score first; drop lowest-score ones if budget is tight.
        var sorted = engrams.OrderByDescending(e => e.Score).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("## Recalled Memories");
        sb.AppendLine();

        foreach (var e in sorted)
        {
            var line = $"- **[{e.Category ?? "memory"}]** {e.Text}";
            var candidate = sb.ToString() + line + "\n";
            if (TokenBudget.Estimate(candidate) > budget)
                break; // No more engrams fit.
            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildUserText(string engramBlock, string triggerPayload, int budget)
    {
        if (string.IsNullOrEmpty(engramBlock))
        {
            return TokenBudget.Estimate(triggerPayload) <= budget
                ? triggerPayload
                : TokenBudget.Truncate(triggerPayload, budget);
        }

        var full = engramBlock + "\n\n---\n\n" + triggerPayload;
        if (TokenBudget.Estimate(full) <= budget)
            return full;

        // Try just the trigger when combined is too large.
        var triggerOnly = TokenBudget.Truncate(triggerPayload, budget);
        return triggerOnly;
    }

    private static IReadOnlyList<InferenceMessage> TrimHistory(
        IReadOnlyList<InferenceMessage> history,
        int budget)
    {
        if (history.Count == 0 || budget <= 0)
            return Array.Empty<InferenceMessage>();

        // Walk from newest to oldest, keep messages until budget is exhausted.
        var kept = new List<InferenceMessage>(history.Count);
        int used = 0;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            var cost = TokenBudget.Estimate(msg.Content);
            if (used + cost > budget)
                break; // Oldest history that doesn't fit is dropped.
            kept.Add(msg);
            used += cost;
        }

        kept.Reverse(); // Restore chronological order.
        return kept;
    }
}
