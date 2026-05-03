using System.Runtime.CompilerServices;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Tests.Fakes;

/// <summary>
/// Deterministic fake of <see cref="IBitNetInferenceClient"/> for unit/integration tests.
/// Configure via the <see cref="EnqueueResponse"/> method (per-tier) or the
/// <see cref="DefaultResponse"/> property (catch-all).
/// </summary>
public sealed class FakeBitNetClient : IBitNetInferenceClient
{
    private readonly Dictionary<ModelTier, Queue<string>> _responses = new();
    public string DefaultResponse { get; set; } = "ok";
    public HashSet<ModelTier> UnavailableTiers { get; } = new();
    public List<InferenceRequest> CapturedRequests { get; } = new();
    public int StreamChunkSize { get; set; } = 8;

    public void EnqueueResponse(ModelTier tier, string text)
    {
        if (!_responses.TryGetValue(tier, out var q))
            _responses[tier] = q = new Queue<string>();
        q.Enqueue(text);
    }

    public Task<bool> IsAvailableAsync(ModelTier tier, CancellationToken ct = default)
        => Task.FromResult(!UnavailableTiers.Contains(tier));

    public Task<InferenceResponse> CompleteAsync(InferenceRequest request, CancellationToken ct = default)
    {
        CapturedRequests.Add(request);
        var text = NextFor(request.Tier);
        return Task.FromResult(new InferenceResponse(
            Content: text,
            PromptTokens: request.Messages.Sum(m => m.Content.Length / 4),
            CompletionTokens: text.Length / 4,
            Model: request.Tier.ToString(),
            Duration: TimeSpan.FromMilliseconds(1)));
    }

    public async IAsyncEnumerable<InferenceTokenChunk> StreamAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        CapturedRequests.Add(request);
        var text = NextFor(request.Tier);
        var size = Math.Max(1, StreamChunkSize);
        for (var i = 0; i < text.Length; i += size)
        {
            var slice = text.Substring(i, Math.Min(size, text.Length - i));
            var isFinal = i + size >= text.Length;
            yield return new InferenceTokenChunk(slice, isFinal);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
        }
        if (text.Length == 0)
            yield return new InferenceTokenChunk(string.Empty, true);
    }

    private string NextFor(ModelTier tier)
    {
        if (_responses.TryGetValue(tier, out var q) && q.Count > 0)
            return q.Dequeue();
        return DefaultResponse;
    }
}
