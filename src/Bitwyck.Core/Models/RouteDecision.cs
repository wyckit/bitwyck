namespace Bitwyck.Core.Models;

/// <summary>
/// Output of the EnergyManager / ModelCascade. Drives which BitNet tier handles a given turn.
/// </summary>
public sealed record RouteDecision(
    ModelTier SelectedTier,
    double Confidence,
    string Rationale,
    IReadOnlyList<ModelTier>? FallbackChain = null
);
