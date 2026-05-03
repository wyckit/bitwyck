using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bitwyck.Runtime.Inference;

/// <summary>
/// Implements <see cref="IBitNetInferenceClient"/> by talking to a managed
/// <c>llama-server.exe</c> via its OpenAI-compatible HTTP/SSE endpoint.
/// </summary>
public sealed class BitNetServerClient : IBitNetInferenceClient
{
    private readonly BitNetServerHost _host;
    private readonly BitNetOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BitNetServerClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public BitNetServerClient(
        BitNetServerHost host,
        BitNetOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<BitNetServerClient> logger)
    {
        _host = host;
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // IBitNetInferenceClient
    // -------------------------------------------------------------------------

    public async Task<bool> IsAvailableAsync(ModelTier tier, CancellationToken ct = default)
    {
        if (!_options.Tiers.TryGetValue(tier, out var cfg))
            return false;

        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(3);
            var resp = await http.GetAsync(
                $"http://{_options.ServerHost}:{cfg.Port}/health", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<InferenceResponse> CompleteAsync(
        InferenceRequest request, CancellationToken ct = default)
    {
        await _host.EnsureStartedAsync(request.Tier, ct).ConfigureAwait(false);

        var port = _host.GetPort(request.Tier);
        var url = $"http://{_options.ServerHost}:{port}/v1/chat/completions";

        var body = BuildRequestBody(request, stream: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var http = _httpClientFactory.CreateClient();
        var httpResp = await http.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        httpResp.EnsureSuccessStatusCode();

        var result = await httpResp.Content
            .ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Null response from llama-server.");

        sw.Stop();

        var content = result.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        var usage = result.Usage;
        bool truncated = (usage?.CompletionTokens ?? 0) >= request.MaxTokens;

        return new InferenceResponse(
            Content: content,
            PromptTokens: usage?.PromptTokens ?? 0,
            CompletionTokens: usage?.CompletionTokens ?? 0,
            Model: result.Model ?? request.Tier.ToString(),
            Duration: sw.Elapsed,
            Truncated: truncated);
    }

    public async IAsyncEnumerable<InferenceTokenChunk> StreamAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await _host.EnsureStartedAsync(request.Tier, ct).ConfigureAwait(false);

        var port = _host.GetPort(request.Tier);
        var url = $"http://{_options.ServerHost}:{port}/v1/chat/completions";

        var body = BuildRequestBody(request, stream: true);
        var json = JsonSerializer.Serialize(body, JsonOptions);

        var http = _httpClientFactory.CreateClient();
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        using var respMsg = await http.SendAsync(
            reqMsg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        respMsg.EnsureSuccessStatusCode();

        using var stream = await respMsg.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break; // null signals end-of-stream
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var payload = line["data: ".Length..];
            if (payload == "[DONE]") break;

            StreamingChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<StreamingChunk>(payload, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse SSE chunk: {Payload}", payload);
                continue;
            }

            var delta = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (delta is not null)
            {
                var isFinal = chunk?.Choices?.FirstOrDefault()?.FinishReason is not null;
                yield return new InferenceTokenChunk(delta, isFinal);
            }
        }

        // Yield a terminal chunk so callers always see IsFinal=true.
        yield return new InferenceTokenChunk(string.Empty, IsFinal: true);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static object BuildRequestBody(InferenceRequest request, bool stream)
    {
        return new
        {
            model = request.Tier.ToString(),
            messages = request.Messages.Select(m => new
            {
                role = RoleString(m.Role),
                content = m.Content,
            }).ToArray(),
            temperature = request.Temperature,
            top_p = request.TopP,
            max_tokens = request.MaxTokens,
            stream,
            seed = request.Seed,
            stop = request.StopSequences,
        };
    }

    private static string RoleString(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        _ => "user",
    };

    // -------------------------------------------------------------------------
    // Private DTO types for JSON deserialization
    // -------------------------------------------------------------------------

#pragma warning disable CS8618
    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
        [JsonPropertyName("usage")] public UsageDto? Usage { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public MessageDto? Message { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    private sealed class MessageDto
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }

    private sealed class UsageDto
    {
        [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    }

    private sealed class StreamingChunk
    {
        [JsonPropertyName("choices")] public List<StreamingChoice>? Choices { get; set; }
    }

    private sealed class StreamingChoice
    {
        [JsonPropertyName("delta")] public DeltaDto? Delta { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    private sealed class DeltaDto
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
#pragma warning restore CS8618
}
