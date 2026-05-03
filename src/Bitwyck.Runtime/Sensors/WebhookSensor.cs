using System.Text.Json;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bitwyck.Runtime.Sensors;

/// <summary>
/// Inbound webhook sensor that self-hosts a minimal Kestrel listener.
/// POST <c>/sense</c> with JSON body <c>{"payload":"…","source":"…"}</c> to ingest events.
/// </summary>
/// <remarks>
/// Call <see cref="StartAsync"/> before consuming <see cref="ReadAsync"/>.
/// The internal channel is unbounded; back-pressure is caller responsibility.
/// </remarks>
public sealed class WebhookSensor : ISensor, IAsyncDisposable
{
    // Fully qualified to avoid name collision with the 'Channel' property below.
    private readonly System.Threading.Channels.Channel<SensoryEvent> _inboundChannel;

    private WebApplication? _app;
    private bool _started;

    public SensoryChannel Channel => SensoryChannel.Webhook;

    public WebhookSensor()
    {
        _inboundChannel = System.Threading.Channels.Channel.CreateUnbounded<SensoryEvent>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
            });
    }

    // -------------------------------------------------------------------------
    // ISensor
    // -------------------------------------------------------------------------

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(_started);

    /// <summary>
    /// Parses a JSON string directly and writes the resulting event to the channel.
    /// This satisfies <see cref="ISensor.CaptureAsync"/> for interface completeness;
    /// the primary ingestion path is via HTTP POST to <c>/sense</c>.
    /// </summary>
    public Task<SensoryEvent> CaptureAsync(object input, CancellationToken ct = default)
    {
        var json = input as string
            ?? throw new ArgumentException("WebhookSensor.CaptureAsync expects a JSON string.", nameof(input));

        var dto = JsonSerializer.Deserialize<WebhookPayloadDto>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (dto is null || string.IsNullOrWhiteSpace(dto.Payload))
            throw new ArgumentException("JSON must contain a non-empty 'payload' field.", nameof(input));

        var ev = BuildEvent(dto.Payload, dto.Source);
        _inboundChannel.Writer.TryWrite(ev);
        return Task.FromResult(ev);
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>Starts the Kestrel host on <c>127.0.0.1:{port}</c>.</summary>
    public async Task StartAsync(int port = 8090, CancellationToken ct = default)
    {
        if (_started) return;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });

        // Silence the default Kestrel/host console logging so it doesn't pollute
        // the cognitive loop's output.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);

        // Set the listen URL via IWebHostBuilder.UseSetting — available on ConfigureWebHostBuilder.
        builder.WebHost.UseSetting("urls", $"http://127.0.0.1:{port}");

        _app = builder.Build();

        _app.MapPost("/sense", async (HttpContext ctx) =>
        {
            WebhookPayloadDto? dto;
            try
            {
                dto = await ctx.Request.ReadFromJsonAsync<WebhookPayloadDto>(ct)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Results.BadRequest("Invalid JSON.");
            }

            if (dto is null || string.IsNullOrWhiteSpace(dto.Payload))
                return Results.BadRequest("'payload' field is required.");

            var ev = BuildEvent(dto.Payload, dto.Source);
            await _inboundChannel.Writer.WriteAsync(ev, ct).ConfigureAwait(false);
            return Results.Accepted();
        });

        await _app.StartAsync(ct).ConfigureAwait(false);
        _started = true;
    }

    /// <summary>Stops the Kestrel host.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_app is not null)
        {
            await _app.StopAsync(ct).ConfigureAwait(false);
            _started = false;
        }
    }

    /// <summary>Streams <see cref="SensoryEvent"/> instances as they arrive via HTTP.</summary>
    public async IAsyncEnumerable<SensoryEvent> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in _inboundChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return ev;
    }

    public async ValueTask DisposeAsync()
    {
        _inboundChannel.Writer.TryComplete();
        if (_app is not null)
            await _app.DisposeAsync().ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static SensoryEvent BuildEvent(string payload, string? source) =>
        new(
            Guid.NewGuid().ToString("N"),
            SensoryChannel.Webhook,
            payload,
            DateTimeOffset.UtcNow,
            source is null ? null : new Dictionary<string, string> { ["source"] = source });

    private sealed record WebhookPayloadDto(string? Payload, string? Source);
}
