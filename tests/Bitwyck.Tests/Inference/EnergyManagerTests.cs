using Bitwyck.Core.Models;
using Bitwyck.Runtime.Inference;

namespace Bitwyck.Tests.Inference;

public sealed class EnergyManagerTests
{
    private static readonly EnergyManager Manager = new();
    private static readonly UserIdentityState Identity = UserIdentityState.Empty();

    // Helper: build a SensoryEvent with a payload of a given length.
    private static SensoryEvent MakeEvent(int payloadLength, Dictionary<string, string>? metadata = null)
    {
        var payload = new string('x', payloadLength);
        return new SensoryEvent(
            Guid.NewGuid().ToString("N"),
            SensoryChannel.Text,
            payload,
            DateTimeOffset.UtcNow,
            metadata);
    }

    // Helper: build a single Engram with the given score.
    private static Engram MakeEngram(double score)
        => new Engram("e1", "ns", "some memory", "memory", EngramLifecycle.Stm, score);

    // ── High-confidence recall → Reflex_1B ───────────────────────────────────

    [Fact]
    public void Route_HighRecallScore_RoutesToReflex()
    {
        // Score >= 0.85 triggers the "high recall" path.
        var recalled = new[] { MakeEngram(0.9) };
        var trigger = MakeEvent(100);

        var decision = Manager.Route(trigger, recalled, Identity);

        Assert.Equal(ModelTier.Reflex_1B, decision.SelectedTier);
    }

    [Fact]
    public void Route_RecallScoreExactlyAtThreshold_RoutesToReflex()
    {
        // 0.85 == HighRecallScore, boundary condition.
        var recalled = new[] { MakeEngram(0.85) };
        var trigger = MakeEvent(100);

        var decision = Manager.Route(trigger, recalled, Identity);

        Assert.Equal(ModelTier.Reflex_1B, decision.SelectedTier);
    }

    // ── No recall + any payload → DeepReason_10B ─────────────────────────────

    [Fact]
    public void Route_NoRecall_RoutesToDeepReason()
    {
        var trigger = MakeEvent(50); // short payload — but no recall
        var decision = Manager.Route(trigger, Array.Empty<Engram>(), Identity);

        Assert.Equal(ModelTier.DeepReason_10B, decision.SelectedTier);
    }

    // ── Empty recall + short payload → DeepReason_10B ────────────────────────

    [Fact]
    public void Route_EmptyRecallShortPayload_RoutesToDeepReason()
    {
        var trigger = MakeEvent(50); // < 100 chars
        var decision = Manager.Route(trigger, Array.Empty<Engram>(), Identity);

        Assert.Equal(ModelTier.DeepReason_10B, decision.SelectedTier);
    }

    // ── Long payload (> 1500 chars) → DeepReason_10B, even with recall ───────

    [Fact]
    public void Route_LongPayloadWithLowRecall_RoutesToDeepReason()
    {
        // Low recall score (< 0.85) + long payload (> 1500) → DeepReason
        var recalled = new[] { MakeEngram(0.5) };
        var trigger = MakeEvent(1501);

        var decision = Manager.Route(trigger, recalled, Identity);

        Assert.Equal(ModelTier.DeepReason_10B, decision.SelectedTier);
    }

    // ── Medium payload (> 400, ≤ 1500) with low recall → Deliberate_7B ───────

    [Fact]
    public void Route_MediumPayloadWithLowRecall_RoutesToDeliberate()
    {
        // 500 chars > MediumPayloadThreshold(400) but < LongPayloadThreshold(1500)
        var recalled = new[] { MakeEngram(0.5) };
        var trigger = MakeEvent(500);

        var decision = Manager.Route(trigger, recalled, Identity);

        Assert.Equal(ModelTier.Deliberate_7B, decision.SelectedTier);
    }

    [Fact]
    public void Route_PayloadJustAboveMediumThreshold_RoutesToDeliberate()
    {
        var recalled = new[] { MakeEngram(0.4) };
        var trigger = MakeEvent(401); // just above 400

        var decision = Manager.Route(trigger, recalled, Identity);

        Assert.Equal(ModelTier.Deliberate_7B, decision.SelectedTier);
    }

    // ── Short payload (< 400 chars) with low recall → Standard_3B ────────────

    [Fact]
    public void Route_ShortPayloadWithLowRecall_RoutesToStandard()
    {
        // < 400 chars + recall present but low score → Standard_3B
        var recalled = new[] { MakeEngram(0.4) };
        var trigger = MakeEvent(200);

        var decision = Manager.Route(trigger, recalled, Identity);

        Assert.Equal(ModelTier.Standard_3B, decision.SelectedTier);
    }

    [Fact]
    public void Route_VeryShortPayloadWithRecall_RoutesToStandard()
    {
        var recalled = new[] { MakeEngram(0.5) };
        var trigger = MakeEvent(50);

        var decision = Manager.Route(trigger, recalled, Identity);

        Assert.Equal(ModelTier.Standard_3B, decision.SelectedTier);
    }

    // ── FallbackChain is always non-null ─────────────────────────────────────

    [Theory]
    [InlineData(0.9, 100)]      // High recall → Reflex
    [InlineData(0.0, 50)]       // No recall path (empty recalled handled separately)
    [InlineData(0.5, 500)]      // Medium → Deliberate
    [InlineData(0.4, 200)]      // Short → Standard
    [InlineData(0.4, 1501)]     // Long → DeepReason
    public void Route_Always_FallbackChainIsNotNull(double score, int payloadLen)
    {
        IReadOnlyList<Engram> recalled = score > 0
            ? new[] { MakeEngram(score) }
            : Array.Empty<Engram>();
        var trigger = MakeEvent(payloadLen);

        var decision = Manager.Route(trigger, recalled, Identity);

        Assert.NotNull(decision.FallbackChain);
    }

    [Fact]
    public void Route_NoRecall_FallbackChainIsNotNull()
    {
        var trigger = MakeEvent(50);
        var decision = Manager.Route(trigger, Array.Empty<Engram>(), Identity);

        Assert.NotNull(decision.FallbackChain);
    }

    // ── FallbackChain starts with selected tier ───────────────────────────────

    [Fact]
    public void Route_FallbackChainStartsWithSelectedTier()
    {
        var recalled = new[] { MakeEngram(0.5) };
        var trigger = MakeEvent(500); // Deliberate_7B

        var decision = Manager.Route(trigger, recalled, Identity);

        Assert.Equal(decision.SelectedTier, decision.FallbackChain![0]);
    }

    // ── Explicit tier override via metadata ──────────────────────────────────

    [Fact]
    public void Route_ExplicitTierOverrideInMetadata_UsesThatTier()
    {
        var metadata = new Dictionary<string, string> { ["tier"] = "Standard_3B" };
        var trigger = MakeEvent(50, metadata);
        var recalled = new[] { MakeEngram(0.9) }; // High recall would normally pick Reflex

        var decision = Manager.Route(trigger, recalled, Identity);

        // The override should win over recall routing.
        Assert.Equal(ModelTier.Standard_3B, decision.SelectedTier);
    }

    // ── Rationale is always non-null/non-empty ───────────────────────────────

    [Fact]
    public void Route_Always_RationaleIsNotEmpty()
    {
        var recalled = new[] { MakeEngram(0.5) };
        var trigger = MakeEvent(200);

        var decision = Manager.Route(trigger, recalled, Identity);

        Assert.False(string.IsNullOrWhiteSpace(decision.Rationale));
    }

    // ── Confidence is between 0 and 1 ────────────────────────────────────────

    [Fact]
    public void Route_Always_ConfidenceIsBetweenZeroAndOne()
    {
        var recalled = new[] { MakeEngram(0.9) };
        var trigger = MakeEvent(100);

        var decision = Manager.Route(trigger, recalled, Identity);

        Assert.InRange(decision.Confidence, 0.0, 1.0);
    }
}
