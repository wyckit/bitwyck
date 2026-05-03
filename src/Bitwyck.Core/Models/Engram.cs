namespace Bitwyck.Core.Models;

public enum EngramLifecycle
{
    Stm = 0,
    Ltm = 1,
    Archived = 2
}

/// <summary>
/// A unit of memory retrieved from or written to the engram store.
/// </summary>
public sealed record Engram(
    string Id,
    string Namespace,
    string Text,
    string? Category = null,
    EngramLifecycle Lifecycle = EngramLifecycle.Stm,
    double Score = 0.0,
    DateTimeOffset? Timestamp = null,
    IReadOnlyDictionary<string, string>? Metadata = null
);
