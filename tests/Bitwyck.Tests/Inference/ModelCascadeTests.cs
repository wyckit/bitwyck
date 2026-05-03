using Bitwyck.Core.Models;
using Bitwyck.Runtime.Inference;

namespace Bitwyck.Tests.Inference;

public sealed class ModelCascadeTests
{
    // Canonical descending order from heaviest to lightest.
    private static readonly ModelTier[] FullOrder =
    [
        ModelTier.DeepReason_10B,
        ModelTier.Deliberate_7B,
        ModelTier.Standard_3B,
        ModelTier.Reflex_1B,
    ];

    // ── BuildChain(DeepReason_10B) → all four tiers in order ────────────────

    [Fact]
    public void BuildChain_DeepReason_StartsWithDeepReasonAndIncludesAllFourTiers()
    {
        var chain = ModelCascade.BuildChain(ModelTier.DeepReason_10B);

        Assert.Equal(4, chain.Count);
        Assert.Equal(ModelTier.DeepReason_10B, chain[0]);
        Assert.Equal(ModelTier.Deliberate_7B, chain[1]);
        Assert.Equal(ModelTier.Standard_3B, chain[2]);
        Assert.Equal(ModelTier.Reflex_1B, chain[3]);
    }

    // ── BuildChain(Reflex_1B) → only [Reflex_1B] ────────────────────────────

    [Fact]
    public void BuildChain_Reflex_ReturnsOnlyReflex()
    {
        var chain = ModelCascade.BuildChain(ModelTier.Reflex_1B);

        Assert.Single(chain);
        Assert.Equal(ModelTier.Reflex_1B, chain[0]);
    }

    // ── BuildChain for mid-level tiers ───────────────────────────────────────

    [Fact]
    public void BuildChain_Deliberate_StartsAtDeliberateAndDescends()
    {
        var chain = ModelCascade.BuildChain(ModelTier.Deliberate_7B);

        Assert.Equal(3, chain.Count);
        Assert.Equal(ModelTier.Deliberate_7B, chain[0]);
        Assert.Equal(ModelTier.Standard_3B, chain[1]);
        Assert.Equal(ModelTier.Reflex_1B, chain[2]);
    }

    [Fact]
    public void BuildChain_Standard_StartsAtStandardAndDescends()
    {
        var chain = ModelCascade.BuildChain(ModelTier.Standard_3B);

        Assert.Equal(2, chain.Count);
        Assert.Equal(ModelTier.Standard_3B, chain[0]);
        Assert.Equal(ModelTier.Reflex_1B, chain[1]);
    }

    // ── Primary tier is always the first element ─────────────────────────────

    [Theory]
    [InlineData(ModelTier.DeepReason_10B)]
    [InlineData(ModelTier.Deliberate_7B)]
    [InlineData(ModelTier.Standard_3B)]
    [InlineData(ModelTier.Reflex_1B)]
    public void BuildChain_PrimaryTierIsAlwaysFirstElement(ModelTier primary)
    {
        var chain = ModelCascade.BuildChain(primary);

        Assert.Equal(primary, chain[0]);
    }

    // ── Chain is in strictly descending tier order ────────────────────────────

    [Theory]
    [InlineData(ModelTier.DeepReason_10B)]
    [InlineData(ModelTier.Deliberate_7B)]
    [InlineData(ModelTier.Standard_3B)]
    [InlineData(ModelTier.Reflex_1B)]
    public void BuildChain_TiersAreInDescendingOrder(ModelTier primary)
    {
        var chain = ModelCascade.BuildChain(primary);

        // Verify each tier's position in FullOrder is strictly increasing.
        for (int i = 1; i < chain.Count; i++)
        {
            var prevPos = Array.IndexOf(FullOrder, chain[i - 1]);
            var currPos = Array.IndexOf(FullOrder, chain[i]);
            Assert.True(prevPos < currPos,
                $"Tier at position {i - 1} ({chain[i - 1]}) should be heavier than {chain[i]}.");
        }
    }

    // ── ResolveAsync: returns first available tier ────────────────────────────

    [Fact]
    public async Task ResolveAsync_AllAvailable_ReturnsDeepReasonWhenPrimaryIsDeepReason()
    {
        var result = await ModelCascade.ResolveAsync(
            ModelTier.DeepReason_10B,
            (_, _) => Task.FromResult(true));

        Assert.Equal(ModelTier.DeepReason_10B, result);
    }

    [Fact]
    public async Task ResolveAsync_PrimaryUnavailable_FallsBackToNextTier()
    {
        var unavailable = new HashSet<ModelTier> { ModelTier.DeepReason_10B };

        var result = await ModelCascade.ResolveAsync(
            ModelTier.DeepReason_10B,
            (tier, _) => Task.FromResult(!unavailable.Contains(tier)));

        Assert.Equal(ModelTier.Deliberate_7B, result);
    }

    [Fact]
    public async Task ResolveAsync_FirstThreeUnavailable_FallsBackToReflex()
    {
        var unavailable = new HashSet<ModelTier>
        {
            ModelTier.DeepReason_10B,
            ModelTier.Deliberate_7B,
            ModelTier.Standard_3B,
        };

        var result = await ModelCascade.ResolveAsync(
            ModelTier.DeepReason_10B,
            (tier, _) => Task.FromResult(!unavailable.Contains(tier)));

        Assert.Equal(ModelTier.Reflex_1B, result);
    }

    [Fact]
    public async Task ResolveAsync_AllUnavailable_ReturnsNull()
    {
        var result = await ModelCascade.ResolveAsync(
            ModelTier.DeepReason_10B,
            (_, _) => Task.FromResult(false));

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_Reflex_WhenAvailable_ReturnsReflex()
    {
        var result = await ModelCascade.ResolveAsync(
            ModelTier.Reflex_1B,
            (_, _) => Task.FromResult(true));

        Assert.Equal(ModelTier.Reflex_1B, result);
    }
}
