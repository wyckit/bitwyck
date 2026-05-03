namespace Bitwyck.Core.Models;

/// <summary>
/// Compressed cross-session model of the user / environment.
/// Updated nightly by IdentityStateUpdater from the day's episodic engrams.
/// </summary>
public sealed record UserIdentityState(
    string Persona,
    IReadOnlyList<string> Preferences,
    IReadOnlyList<string> EvolvingGoals,
    IReadOnlyList<string> Constraints,
    IReadOnlyDictionary<string, string> KnownFacts,
    DateTimeOffset LastUpdated,
    int Version
)
{
    public static UserIdentityState Empty() => new(
        Persona: "Unknown user — observe and adapt.",
        Preferences: Array.Empty<string>(),
        EvolvingGoals: Array.Empty<string>(),
        Constraints: Array.Empty<string>(),
        KnownFacts: new Dictionary<string, string>(),
        LastUpdated: DateTimeOffset.UtcNow,
        Version: 0);

    public string ToPromptBlock()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# User Identity (v{Version}, {LastUpdated:yyyy-MM-dd})");
        sb.AppendLine($"Persona: {Persona}");
        if (Preferences.Count > 0)
        {
            sb.AppendLine("Preferences:");
            foreach (var p in Preferences) sb.AppendLine($"  - {p}");
        }
        if (EvolvingGoals.Count > 0)
        {
            sb.AppendLine("Goals:");
            foreach (var g in EvolvingGoals) sb.AppendLine($"  - {g}");
        }
        if (Constraints.Count > 0)
        {
            sb.AppendLine("Constraints:");
            foreach (var c in Constraints) sb.AppendLine($"  - {c}");
        }
        return sb.ToString();
    }
}
