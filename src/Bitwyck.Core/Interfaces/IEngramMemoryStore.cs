using Bitwyck.Core.Models;

namespace Bitwyck.Core.Interfaces;

public sealed record EngramQuery(
    string Text,
    string? Namespace = null,
    int K = 5,
    bool Hybrid = true,
    bool ExpandGraph = false,
    string? Category = null,
    double MinScore = 0.0
);

public interface IEngramMemoryStore
{
    Task<IReadOnlyList<Engram>> SearchAsync(EngramQuery query, CancellationToken ct = default);

    Task StoreAsync(Engram engram, CancellationToken ct = default);

    Task<bool> DeleteAsync(string id, string @namespace, CancellationToken ct = default);

    Task<int> CountAsync(string? @namespace = null, CancellationToken ct = default);
}
