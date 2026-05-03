using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bitwyck.Runtime.Memory;

/// <summary>
/// Implements <see cref="IEngramMemoryStore"/> by delegating to a
/// <see cref="CognitiveIndex"/> from McpEngramMemory.Core.
/// </summary>
public sealed class EngramAdapter : IEngramMemoryStore, IDisposable
{
    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedder;
    private readonly IStorageProvider _storage;
    private readonly string _defaultNamespace;

    public EngramAdapter(EngramOptions options, ILogger<EngramAdapter>? log = null)
    {
        _defaultNamespace = options.DefaultNamespace;

        // Ensure the database directory exists.
        var dir = Path.GetDirectoryName(options.DatabasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _storage = new SqliteStorageProvider(
            options.DatabasePath,
            1,
            NullLogger<SqliteStorageProvider>.Instance);

        _embedder = options.EmbeddingModelPath is not null
            ? new OnnxEmbeddingService(options.EmbeddingModelPath, 512)
            : new HashFallbackEmbedder();

        var limits = new MemoryLimitsConfig(
            100_000,
            1_000_000);
        _index = new CognitiveIndex(_storage, limits);
    }

    // ── IEngramMemoryStore ────────────────────────────────────────────────────

    public Task<IReadOnlyList<Engram>> SearchAsync(EngramQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ns = query.Namespace ?? _defaultNamespace;
        var vec = _embedder.Embed(query.Text);
        var req = new SearchRequest
        {
            Query = vec,
            QueryText = query.Text,
            Namespace = ns,
            K = query.K,
            Hybrid = query.Hybrid,
            MinScore = (float)query.MinScore,
            Category = query.Category,
        };

        var results = _index.Search(req);
        IReadOnlyList<Engram> engrams = results
            .Select(r => ToEngram(r, ns))
            .ToArray();
        return Task.FromResult(engrams);
    }

    public Task StoreAsync(Engram engram, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var vec = _embedder.Embed(engram.Text);
        var metadata = engram.Metadata is null
            ? null
            : new Dictionary<string, string>(engram.Metadata);

        var entry = new CognitiveEntry(
            id: engram.Id,
            vector: vec,
            ns: engram.Namespace,
            text: engram.Text,
            category: engram.Category ?? string.Empty,
            metadata: metadata,
            lifecycleState: LifecycleToString(engram.Lifecycle),
            keywords: engram.Category ?? string.Empty);

        _index.Upsert(entry);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, string @namespace, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ok = _index.Delete(id);
        return Task.FromResult(ok);
    }

    public Task<int> CountAsync(string? @namespace = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var count = @namespace is null
            ? _index.Count
            : _index.CountInNamespace(@namespace);
        return Task.FromResult(count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Engram ToEngram(CognitiveSearchResult r, string ns) =>
        new(
            Id: r.Id,
            Namespace: ns,
            Text: r.Text ?? string.Empty,
            Category: string.IsNullOrEmpty(r.Category) ? null : r.Category,
            Lifecycle: ParseLifecycle(r.LifecycleState),
            Score: r.Score,
            Timestamp: null,
            Metadata: r.Metadata is { Count: > 0 }
                ? (IReadOnlyDictionary<string, string>)r.Metadata
                : null);

    private static string LifecycleToString(EngramLifecycle lc) => lc switch
    {
        EngramLifecycle.Ltm => "ltm",
        EngramLifecycle.Archived => "archived",
        _ => "stm",
    };

    private static EngramLifecycle ParseLifecycle(string? state) => state switch
    {
        "ltm" => EngramLifecycle.Ltm,
        "archived" => EngramLifecycle.Archived,
        _ => EngramLifecycle.Stm,
    };

    public void Dispose()
    {
        _index.Dispose();
        _storage.Dispose();
    }

    // ── Fallback embedder (hash-based, deterministic) ─────────────────────────

    /// <summary>
    /// Used when no ONNX model path is configured. Produces a 384-dim vector by
    /// distributing a MurmurHash3-inspired mixing of each character into float
    /// buckets. Not semantically meaningful but ensures the build is fully
    /// functional for integration tests that don't require real embeddings.
    /// </summary>
    private sealed class HashFallbackEmbedder : IEmbeddingService
    {
        public int Dimensions => 384;

        public float[] Embed(string text)
        {
            var vec = new float[Dimensions];
            if (string.IsNullOrEmpty(text)) return vec;

            // Spread character hashes across buckets.
            for (int i = 0; i < text.Length; i++)
            {
                uint h = (uint)text[i] * 2654435761u; // Knuth multiplicative hash
                h ^= h >> 16;
                int bucket = (int)(h % (uint)Dimensions);
                vec[bucket] += 1.0f / (i + 1);
            }

            // L2 normalise.
            float norm = MathF.Sqrt(vec.Sum(x => x * x));
            if (norm > 0f)
                for (int i = 0; i < vec.Length; i++) vec[i] /= norm;

            return vec;
        }
    }
}
