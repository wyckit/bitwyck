using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Cognition;

/// <summary>
/// Override <see cref="ICognitiveRouter"/> that always routes to a fixed tier.
/// Useful when the BitNet binary in the local environment is unstable on
/// larger models, or for deterministic benchmarking.
/// </summary>
public sealed class ForcedTierRouter : ICognitiveRouter
{
    private readonly ModelTier _tier;

    public ForcedTierRouter(ModelTier tier) { _tier = tier; }

    public RouteDecision Route(SensoryEvent trigger, IReadOnlyList<Engram> recalled, UserIdentityState identity)
        => new(
            SelectedTier: _tier,
            Confidence: 1.0,
            Rationale: $"Forced tier (Bitwyck:BitNet:ForceTier={_tier})",
            FallbackChain: new[] { _tier });
}
