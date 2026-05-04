using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Cognition;

/// <summary>
/// Source of <see cref="SystemBias"/> for a given turn. Allows the harness to
/// override temperature / persona / risk based on context (e.g. lower temperature
/// for tool-call generation, higher for creative writing tasks).
/// </summary>
public interface ISystemBiasProvider
{
    SystemBias GetBias(SensoryEvent trigger, RouteDecision route);
}

/// <summary>Default provider: tier-aware bias with sensible defaults.</summary>
public sealed class DefaultSystemBiasProvider : ISystemBiasProvider
{
    private readonly SystemBias _baseline;

    public DefaultSystemBiasProvider(SystemBias? baseline = null)
    {
        _baseline = baseline ?? SystemBias.Default();
    }

    public SystemBias GetBias(SensoryEvent trigger, RouteDecision route)
    {
        // BitNet-2B-4T (now mapped to Reflex_1B) doesn't emit tool calls, so
        // we no longer need to clamp temperature low for XML well-formedness.
        // Higher temp prevents the model from settling into stock refusal /
        // repetition loops on borderline factual questions.
        var temp = route.SelectedTier switch
        {
            ModelTier.Reflex_1B => Math.Max(_baseline.Temperature, 0.55),
            ModelTier.Standard_3B => Math.Max(_baseline.Temperature, 0.4),
            ModelTier.Deliberate_7B => _baseline.Temperature,
            ModelTier.DeepReason_10B => Math.Max(_baseline.Temperature, 0.3),
            _ => _baseline.Temperature
        };

        return _baseline with { Temperature = temp };
    }
}

/// <summary>Fixed bias for deterministic testing.</summary>
public sealed class StaticSystemBiasProvider : ISystemBiasProvider
{
    private readonly SystemBias _bias;
    public StaticSystemBiasProvider(SystemBias bias) { _bias = bias; }
    public SystemBias GetBias(SensoryEvent trigger, RouteDecision route) => _bias;
}
