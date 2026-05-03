using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Inference;

/// <summary>
/// Produces an ordered fallback chain for a given primary <see cref="ModelTier"/>.
/// The chain always descends from the selected tier toward the lightest available tier,
/// so heavier work can degrade gracefully if a tier is offline.
/// </summary>
public static class ModelCascade
{
    // Canonical tier order, heaviest → lightest.
    private static readonly ModelTier[] TierOrder =
    [
        ModelTier.DeepReason_10B,
        ModelTier.Deliberate_7B,
        ModelTier.Standard_3B,
        ModelTier.Reflex_1B,
    ];

    /// <summary>
    /// Returns a fallback chain starting from <paramref name="primary"/> and descending
    /// toward lighter tiers. The primary tier is always the first element.
    /// </summary>
    /// <example>
    /// <c>BuildChain(DeepReason_10B)</c> → [DeepReason_10B, Deliberate_7B, Standard_3B, Reflex_1B]
    /// <c>BuildChain(Deliberate_7B)</c>  → [Deliberate_7B, Standard_3B, Reflex_1B]
    /// </example>
    public static IReadOnlyList<ModelTier> BuildChain(ModelTier primary)
    {
        var startIndex = Array.IndexOf(TierOrder, primary);
        if (startIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(primary), $"Unknown tier: {primary}");

        return TierOrder[startIndex..];
    }

    /// <summary>
    /// Returns the first tier in the cascade whose model is available on disk,
    /// according to <paramref name="isAvailable"/>.
    /// </summary>
    public static async Task<ModelTier?> ResolveAsync(
        ModelTier primary,
        Func<ModelTier, CancellationToken, Task<bool>> isAvailable,
        CancellationToken ct = default)
    {
        foreach (var tier in BuildChain(primary))
        {
            if (await isAvailable(tier, ct).ConfigureAwait(false))
                return tier;
        }
        return null;
    }
}
