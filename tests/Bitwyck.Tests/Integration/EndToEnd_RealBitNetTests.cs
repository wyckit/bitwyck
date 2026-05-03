using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Inference;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bitwyck.Tests.Integration;

/// <summary>
/// Live-inference integration test gated by the BITWYCK_ENABLE_LIVE=1 environment variable.
/// This test does NOT run in CI unless the variable is set. It requires a running local
/// llama-server.exe and the Reflex_1B model file on disk.
///
/// Build always; run only when explicitly opted-in.
/// </summary>
public sealed class EndToEnd_RealBitNetTests
{
    [Fact]
    public async Task LiveBitNet_OneShot_ReturnsNonEmpty()
    {
        // Skip when the opt-in env var is not present
        if (Environment.GetEnvironmentVariable("BITWYCK_ENABLE_LIVE") != "1")
            return;

        var options = BitNetOptions.Default();

        // Skip gracefully if the Reflex_1B model file is absent
        if (!options.Tiers.TryGetValue(ModelTier.Reflex_1B, out var tierCfg) ||
            !File.Exists(tierCfg.ModelPath))
            return;

        // Skip gracefully if the server binary is absent
        if (!File.Exists(options.ServerExePath))
            return;

        // Build the host + client manually (no DI container)
        var logger = NullLogger<BitNetServerHost>.Instance;
        var host = new BitNetServerHost(options, logger);

        // Use a simple HttpClientFactory wrapper
        var httpClientLogger = NullLogger<BitNetServerClient>.Instance;
        var httpClientFactory = new SingletonHttpClientFactory();
        var client = new BitNetServerClient(host, options, httpClientFactory, httpClientLogger);

        try
        {
            // Start the Reflex_1B server and wait for readiness
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await host.EnsureStartedAsync(ModelTier.Reflex_1B, cts.Token);

            // Verify availability
            var available = await client.IsAvailableAsync(ModelTier.Reflex_1B, cts.Token);
            Assert.True(available, "Reflex_1B should be available after EnsureStartedAsync");

            // One-shot completion
            var request = new InferenceRequest(
                Tier: ModelTier.Reflex_1B,
                Messages: new[]
                {
                    new InferenceMessage(MessageRole.System, "You are a concise assistant."),
                    new InferenceMessage(MessageRole.User, "Say hello.")
                },
                MaxTokens: 64,
                Temperature: 0.1);

            var response = await client.CompleteAsync(request, cts.Token);

            // Assert a non-empty response was produced
            Assert.NotNull(response);
            Assert.NotEmpty(response.Content);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
        }
    }

    // ── Minimal IHttpClientFactory shim ──────────────────────────────────────

    private sealed class SingletonHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        private readonly System.Net.Http.HttpClient _client = new();
        public System.Net.Http.HttpClient CreateClient(string name) => _client;
    }
}
