using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bitwyck.Runtime.Memory;

/// <summary>
/// Higher-level recall service. Takes a <see cref="SensoryEvent"/> and
/// <see cref="UserIdentityState"/>, constructs a contextualised query, runs
/// hybrid + graph-expanded search against the configured namespace, and returns
/// scored <see cref="Engram"/> records ranked highest-first.
/// </summary>
public sealed class EngramRecallService
{
    private readonly IEngramMemoryStore _store;
    private readonly EngramOptions _options;

    public EngramRecallService(IEngramMemoryStore store, EngramOptions options)
    {
        _store = store;
        _options = options;
    }

    /// <summary>
    /// Recalls up to <paramref name="k"/> engrams relevant to <paramref name="trigger"/>.
    /// The user identity persona and goals are prepended to the query text to bias
    /// retrieval toward memories that are relevant in the user's context.
    /// </summary>
    public async Task<IReadOnlyList<Engram>> RecallAsync(
        SensoryEvent trigger,
        UserIdentityState identity,
        int k = 8,
        double minScore = 0.25,
        CancellationToken ct = default)
    {
        // Construct an enriched query from the trigger payload plus identity context.
        var queryText = BuildQueryText(trigger, identity);

        var query = new EngramQuery(
            Text: queryText,
            Namespace: _options.DefaultNamespace,
            K: k,
            Hybrid: true,
            ExpandGraph: false,  // graph expansion is handled in EngramAdapter layer when needed
            MinScore: minScore);

        var results = await _store.SearchAsync(query, ct).ConfigureAwait(false);

        // Results from the store are already scored; return them ranked by score descending.
        return results
            .OrderByDescending(e => e.Score)
            .ToArray();
    }

    private static string BuildQueryText(SensoryEvent trigger, UserIdentityState identity)
    {
        // Prepend a compact identity prefix so the vector leans toward user-relevant memories.
        if (string.IsNullOrWhiteSpace(identity.Persona))
            return trigger.Payload;

        // Keep the prefix short to avoid drowning out the actual payload.
        var prefix = identity.Persona.Length > 200
            ? identity.Persona[..200]
            : identity.Persona;

        return $"{prefix}\n{trigger.Payload}";
    }
}
