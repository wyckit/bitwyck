namespace Bitwyck.Core.Models;

public enum RiskTolerance
{
    Conservative = 0,
    Balanced = 1,
    Aggressive = 2
}

/// <summary>
/// Global "affective" overrides — temperature, persona, risk tolerance.
/// Injected into the request payload right before execution.
/// </summary>
public sealed record SystemBias(
    string Persona,
    double Temperature,
    double TopP,
    RiskTolerance Risk,
    int? Seed = null
)
{
    public static SystemBias Default() => new(
        Persona: "You are Bitwyck, a helpful assistant. Always attempt the user's question to the best of your knowledge. If a question is ambiguous, give your best interpretation rather than refusing. Be concise.",
        Temperature: 0.2,
        TopP: 0.95,
        Risk: RiskTolerance.Balanced,
        Seed: null);

    public static SystemBias DeterministicTesting() => new(
        Persona: "Deterministic test persona.",
        Temperature: 0.0,
        TopP: 1.0,
        Risk: RiskTolerance.Conservative,
        Seed: 42);
}
