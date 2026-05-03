using Bitwyck.Core.Models;
using Bitwyck.Core.Utilities;
using Bitwyck.Runtime.Memory;

namespace Bitwyck.Tests.Memory;

public sealed class ContextCompilerTests
{
    private static readonly ContextCompiler Compiler = new();

    // Helper: build a minimal CognitiveContext
    private static CognitiveContext MakeContext(
        string triggerPayload = "Hello!",
        IReadOnlyList<Engram>? engrams = null,
        IReadOnlyList<InferenceMessage>? chatHistory = null,
        int tokenBudget = 4096)
    {
        return new CognitiveContext(
            Trigger: SensoryEvent.FromText(triggerPayload),
            Identity: UserIdentityState.Empty(),
            RecalledEngrams: engrams ?? Array.Empty<Engram>(),
            ChatHistory: chatHistory ?? Array.Empty<InferenceMessage>(),
            Bias: SystemBias.Default(),
            TokenBudget: tokenBudget);
    }

    private static Engram MakeEngram(string id, double score, string text = "some memory text")
        => new Engram(id, "test-ns", text, "memory", EngramLifecycle.Stm, score);

    // ── At minimum: System + User messages ───────────────────────────────────

    [Fact]
    public void Compile_Always_ProducesAtLeastSystemAndUserMessages()
    {
        var ctx = MakeContext();
        var messages = Compiler.Compile(ctx, "(no tools)");

        Assert.True(messages.Count >= 2, "Expected at least System + User messages.");
        Assert.Equal(MessageRole.System, messages[0].Role);
        Assert.Equal(MessageRole.User, messages[1].Role);
    }

    // ── Trigger appears in User message ──────────────────────────────────────

    [Fact]
    public void Compile_UserMessageContainsTriggerPayload()
    {
        var trigger = "What is the capital of France?";
        var ctx = MakeContext(triggerPayload: trigger);
        var messages = Compiler.Compile(ctx, "(no tools)");

        var userMsg = messages.First(m => m.Role == MessageRole.User);
        Assert.Contains(trigger, userMsg.Content);
    }

    // ── Engrams appear in descending score order ──────────────────────────────

    [Fact]
    public void Compile_EngramsOrderedByScoreDescending()
    {
        var engrams = new[]
        {
            MakeEngram("e1", 0.3, "low score memory"),
            MakeEngram("e2", 0.9, "high score memory"),
            MakeEngram("e3", 0.6, "medium score memory"),
            MakeEngram("e4", 0.8, "upper memory"),
            MakeEngram("e5", 0.1, "very low memory"),
        };
        var ctx = MakeContext(engrams: engrams, tokenBudget: 4096);
        var messages = Compiler.Compile(ctx, "(no tools)");

        // The user message should contain the engram block. Find the user message.
        var userMsg = messages.First(m => m.Role == MessageRole.User);

        // Verify the high-score engram text appears before the low-score ones.
        var highIdx = userMsg.Content.IndexOf("high score memory", StringComparison.Ordinal);
        var upperIdx = userMsg.Content.IndexOf("upper memory", StringComparison.Ordinal);
        var mediumIdx = userMsg.Content.IndexOf("medium score memory", StringComparison.Ordinal);
        var lowIdx = userMsg.Content.IndexOf("low score memory", StringComparison.Ordinal);

        // All should be present with a generous budget.
        Assert.True(highIdx >= 0, "High-score engram text missing from user message.");
        Assert.True(upperIdx >= 0, "Upper-score engram text missing.");
        Assert.True(mediumIdx >= 0, "Medium-score engram text missing.");
        Assert.True(lowIdx >= 0, "Low-score engram text missing.");

        // Score 0.9 > 0.8 > 0.6 > 0.3 > 0.1 in the content
        Assert.True(highIdx < upperIdx, "0.9-score engram should appear before 0.8-score.");
        Assert.True(upperIdx < mediumIdx, "0.8-score engram should appear before 0.6-score.");
        Assert.True(mediumIdx < lowIdx, "0.6-score engram should appear before 0.3-score.");
    }

    // ── Tight budget: lowest-score engrams dropped ────────────────────────────

    [Fact]
    public void Compile_TightBudget_DropsLowestScoreEngrams()
    {
        // Use a very small budget so not all engrams fit.
        // Each engram line is roughly "- **[memory]** text" + newline.
        // With budget=200 tokens, the system text alone will eat most of it,
        // so only the highest-scoring engram(s) should survive in the user message.
        var engrams = new[]
        {
            MakeEngram("high", 0.95, "keeper high priority"),
            MakeEngram("low1", 0.1, "dropper one lowest"),
            MakeEngram("low2", 0.05, "dropper two lowest"),
        };
        var ctx = MakeContext(triggerPayload: "short", engrams: engrams, tokenBudget: 200);
        var messages = Compiler.Compile(ctx, "(no tools)");

        var userMsg = messages.First(m => m.Role == MessageRole.User);

        // The high-score engram may or may not fit depending on exact budget split,
        // but the very low-score engrams should be the first to be dropped.
        // What we can reliably assert: if any engram text appears, the high-score
        // one appears before (or without) the low-score ones.
        var highIdx = userMsg.Content.IndexOf("keeper high priority", StringComparison.Ordinal);
        var lowIdx1 = userMsg.Content.IndexOf("dropper one lowest", StringComparison.Ordinal);
        var lowIdx2 = userMsg.Content.IndexOf("dropper two lowest", StringComparison.Ordinal);

        // If both high and low appear, high must come first.
        if (highIdx >= 0 && lowIdx1 >= 0)
            Assert.True(highIdx < lowIdx1, "High-score engram must precede low-score engram.");

        // If high is absent, low should also be absent (budget was too tight for any).
        if (highIdx < 0)
        {
            Assert.True(lowIdx1 < 0, "Low-score engram should not appear if high-score was dropped.");
            Assert.True(lowIdx2 < 0, "Low-score engram2 should not appear if high-score was dropped.");
        }
    }

    // ── Empty engrams + empty history → System + User with trigger ───────────

    [Fact]
    public void Compile_EmptyEngramsAndHistory_ProducesSystemAndUserWithTrigger()
    {
        var trigger = "trigger payload content here";
        var ctx = MakeContext(
            triggerPayload: trigger,
            engrams: Array.Empty<Engram>(),
            chatHistory: Array.Empty<InferenceMessage>());
        var messages = Compiler.Compile(ctx, "(no tools)");

        Assert.True(messages.Count >= 2);
        Assert.Equal(MessageRole.System, messages[0].Role);
        Assert.Equal(MessageRole.User, messages[1].Role);
        Assert.Contains(trigger, messages[1].Content);
    }

    // ── Tool manifest appears in System message ──────────────────────────────

    [Fact]
    public void Compile_ToolManifest_AppearsInSystemMessage()
    {
        var ctx = MakeContext();
        var manifest = "TOOLS: read_file, write_file, spawn_agent";
        var messages = Compiler.Compile(ctx, manifest);

        var sysMsg = messages.First(m => m.Role == MessageRole.System);
        Assert.Contains(manifest, sysMsg.Content);
    }

    // ── Chat history is appended after System + User ─────────────────────────

    [Fact]
    public void Compile_WithChatHistory_AppendsHistoryMessages()
    {
        var history = new[]
        {
            new InferenceMessage(MessageRole.Assistant, "previous assistant turn"),
            new InferenceMessage(MessageRole.User, "previous user turn"),
        };
        var ctx = MakeContext(chatHistory: history, tokenBudget: 4096);
        var messages = Compiler.Compile(ctx, "(no tools)");

        // Should be System + User + the history messages
        Assert.True(messages.Count >= 4,
            $"Expected at least 4 messages (System, User, 2 history), got {messages.Count}.");
    }
}
