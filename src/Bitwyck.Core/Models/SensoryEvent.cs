namespace Bitwyck.Core.Models;

public enum SensoryChannel
{
    Text = 0,
    Webhook = 1,
    Audio = 2,
    Vision = 3,
    ChronoTrigger = 4,
    SpawnedAgent = 5
}

/// <summary>
/// Normalized output of a sensor. The first stage of the cognitive cycle.
/// </summary>
public sealed record SensoryEvent(
    string Id,
    SensoryChannel Channel,
    string Payload,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string>? Metadata = null
)
{
    public static SensoryEvent FromText(string text, string? source = null) =>
        new(Guid.NewGuid().ToString("N"), SensoryChannel.Text, text, DateTimeOffset.UtcNow,
            source is null ? null : new Dictionary<string, string> { ["source"] = source });
}
