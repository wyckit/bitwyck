using Bitwyck.Core.Models;

namespace Bitwyck.Core.Interfaces;

/// <summary>
/// Abstraction over BitNet inference. Implementations:
/// - BitNetServerClient: HTTP/SSE to a managed llama-server.exe.
/// - BitNetCliClient: shell-out to llama-cli.exe (offline / fallback).
/// </summary>
public interface IBitNetInferenceClient
{
    /// <summary>True if the client can serve the requested tier (e.g. server is up, model is loaded).</summary>
    Task<bool> IsAvailableAsync(ModelTier tier, CancellationToken ct = default);

    Task<InferenceResponse> CompleteAsync(InferenceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Streaming inference. Each yielded chunk contains a token and an IsFinal marker.
    /// The harness consumes this for tool-call interception.
    /// </summary>
    IAsyncEnumerable<InferenceTokenChunk> StreamAsync(InferenceRequest request, CancellationToken ct = default);
}
