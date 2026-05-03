using Bitwyck.Core.Models;
using Bitwyck.Runtime.Cognition;

namespace Bitwyck.Tests.Cognition;

public class SystemBiasTests
{
    private static SensoryEvent DummyEvent =>
        SensoryEvent.FromText("test input");

    // ── Reflex_1B: temperature clamped ≤ 0.15 ────────────────────────────────

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.15)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Reflex1B_TemperatureClampedToAtMost015(double baselineTemp)
    {
        var baseline = new SystemBias("persona", baselineTemp, 0.95, RiskTolerance.Balanced);
        var provider = new DefaultSystemBiasProvider(baseline);
        var route = new RouteDecision(ModelTier.Reflex_1B, 0.9, "fast path");

        var bias = provider.GetBias(DummyEvent, route);

        Assert.True(bias.Temperature <= 0.15,
            $"Expected temperature ≤ 0.15 for Reflex_1B but got {bias.Temperature}");
    }

    // ── DeepReason_10B: temperature ≥ max(baseline, 0.3) ─────────────────────

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(0.3)]
    [InlineData(0.5)]
    public void DeepReason10B_TemperatureAtLeast03(double baselineTemp)
    {
        var baseline = new SystemBias("persona", baselineTemp, 0.95, RiskTolerance.Balanced);
        var provider = new DefaultSystemBiasProvider(baseline);
        var route = new RouteDecision(ModelTier.DeepReason_10B, 0.9, "deep reason");

        var bias = provider.GetBias(DummyEvent, route);

        var expectedMin = Math.Max(baselineTemp, 0.3);
        Assert.True(bias.Temperature >= expectedMin,
            $"Expected temperature ≥ {expectedMin} for DeepReason_10B but got {bias.Temperature}");
    }

    // ── Standard_3B and Deliberate_7B: temperature passes through unchanged ──

    [Theory]
    [InlineData(ModelTier.Standard_3B, 0.7)]
    [InlineData(ModelTier.Deliberate_7B, 0.4)]
    public void Standard_And_Deliberate_TemperaturePassesThrough(ModelTier tier, double baselineTemp)
    {
        var baseline = new SystemBias("persona", baselineTemp, 0.95, RiskTolerance.Balanced);
        var provider = new DefaultSystemBiasProvider(baseline);
        var route = new RouteDecision(tier, 0.9, "standard path");

        var bias = provider.GetBias(DummyEvent, route);

        Assert.Equal(baselineTemp, bias.Temperature, precision: 10);
    }

    // ── Other fields pass through unchanged ───────────────────────────────────

    [Fact]
    public void GetBias_Persona_TopP_Risk_Seed_PassThroughUnchanged()
    {
        var baseline = new SystemBias(
            Persona: "custom-persona",
            Temperature: 0.5,
            TopP: 0.88,
            Risk: RiskTolerance.Conservative,
            Seed: 123);
        var provider = new DefaultSystemBiasProvider(baseline);
        var route = new RouteDecision(ModelTier.Standard_3B, 0.9, "mid tier");

        var bias = provider.GetBias(DummyEvent, route);

        Assert.Equal("custom-persona", bias.Persona);
        Assert.Equal(0.88, bias.TopP, precision: 10);
        Assert.Equal(RiskTolerance.Conservative, bias.Risk);
        Assert.Equal(123, bias.Seed);
    }
}
