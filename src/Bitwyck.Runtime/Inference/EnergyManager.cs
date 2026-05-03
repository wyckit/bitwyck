using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Inference;

/// <summary>
/// Implements <see cref="ICognitiveRouter"/> using a heuristic energy model.
/// Routes turns to progressively heavier tiers based on recall quality,
/// trigger payload length, and explicit overrides carried in the trigger metadata.
/// </summary>
public sealed class EnergyManager : ICognitiveRouter
{
    // Routing thresholds
    private const double HighRecallScore = 0.85;
    private const int LongPayloadThreshold = 1500;
    private const int MediumPayloadThreshold = 400;

    // Confidence values assigned per routing path
    private const double ConfidenceHighRecall = 0.0; // filled from recalled[0].Score
    private const double ConfidenceLowRecall = 0.6;
    private const double ConfidenceDeliberate = 0.7;
    private const double ConfidenceStandard = 0.75;

    // Metadata key for explicit tier override (e.g. "tier" = "DeepReason_10B")
    private const string TierOverrideKey = "tier";

    /// <inheritdoc/>
    public RouteDecision Route(
        SensoryEvent trigger,
        IReadOnlyList<Engram> recalled,
        UserIdentityState identity)
    {
        // --- Explicit override via trigger metadata ---
        if (trigger.Metadata is not null &&
            trigger.Metadata.TryGetValue(TierOverrideKey, out var overrideValue) &&
            Enum.TryParse<ModelTier>(overrideValue, ignoreCase: true, out var overrideTier))
        {
            return MakeDecision(
                overrideTier,
                confidence: 1.0,
                rationale: $"Explicit tier override via metadata key '{TierOverrideKey}': {overrideTier}");
        }

        // --- High-confidence recall → Reflex_1B ---
        if (recalled.Count > 0 && recalled[0].Score >= HighRecallScore)
        {
            return MakeDecision(
                ModelTier.Reflex_1B,
                confidence: recalled[0].Score,
                rationale: $"High recall score ({recalled[0].Score:F3} >= {HighRecallScore}) — Reflex tier sufficient.");
        }

        // --- No recall or very long payload → DeepReason_10B ---
        if (recalled.Count == 0 || trigger.Payload.Length > LongPayloadThreshold)
        {
            var reason = recalled.Count == 0
                ? "No recalled engrams — novel problem requires deep reasoning."
                : $"Long payload ({trigger.Payload.Length} chars > {LongPayloadThreshold}) — deep reasoning required.";

            return MakeDecision(
                ModelTier.DeepReason_10B,
                confidence: ConfidenceLowRecall,
                rationale: reason);
        }

        // --- Medium payload → Deliberate_7B ---
        if (trigger.Payload.Length > MediumPayloadThreshold)
        {
            return MakeDecision(
                ModelTier.Deliberate_7B,
                confidence: ConfidenceDeliberate,
                rationale: $"Medium payload ({trigger.Payload.Length} chars > {MediumPayloadThreshold}) — deliberate reasoning selected.");
        }

        // --- Default → Standard_3B ---
        return MakeDecision(
            ModelTier.Standard_3B,
            confidence: ConfidenceStandard,
            rationale: "Short payload with recall available — standard conversational tier.");
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static RouteDecision MakeDecision(
        ModelTier selected, double confidence, string rationale)
    {
        var fallback = ModelCascade.BuildChain(selected);
        return new RouteDecision(selected, confidence, rationale, fallback);
    }
}
