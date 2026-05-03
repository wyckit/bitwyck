using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Tests.Fakes;

/// <summary>
/// Trivial in-memory <see cref="IEngramMemoryStore"/> that does substring-match
/// scoring. Sufficient for tests that don't depend on real semantic recall.
/// </summary>
public sealed class InMemoryEngramStore : IEngramMemoryStore
{
    private readonly List<Engram> _entries = new();
    private readonly object _lock = new();

    public Task<IReadOnlyList<Engram>> SearchAsync(EngramQuery query, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var ns = query.Namespace;
            IEnumerable<Engram> pool = _entries;
            if (ns is not null) pool = pool.Where(e => e.Namespace == ns);
            if (query.Category is not null) pool = pool.Where(e => e.Category == query.Category);

            var queryTokens = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var scored = pool
                .Select(e =>
                {
                    var hits = queryTokens.Count(t => e.Text.Contains(t, StringComparison.OrdinalIgnoreCase));
                    var score = queryTokens.Length == 0 ? 0.0 : (double)hits / queryTokens.Length;
                    return e with { Score = score };
                })
                .Where(e => e.Score >= query.MinScore)
                .OrderByDescending(e => e.Score)
                .Take(query.K)
                .ToList();

            return Task.FromResult<IReadOnlyList<Engram>>(scored);
        }
    }

    public Task StoreAsync(Engram engram, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Id == engram.Id && e.Namespace == engram.Namespace);
            _entries.Add(engram);
        }
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, string @namespace, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var removed = _entries.RemoveAll(e => e.Id == id && e.Namespace == @namespace);
            return Task.FromResult(removed > 0);
        }
    }

    public Task<int> CountAsync(string? @namespace = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(@namespace is null ? _entries.Count : _entries.Count(e => e.Namespace == @namespace));
        }
    }
}
