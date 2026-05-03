using System.Collections.Concurrent;
using System.Diagnostics;
using Bitwyck.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bitwyck.Runtime.Inference;

/// <summary>
/// Background service that manages <c>llama-server.exe</c> child processes,
/// one per <see cref="ModelTier"/>. Non-lazy tiers are started on host startup;
/// lazy tiers are started on first demand via <see cref="EnsureStartedAsync"/>.
/// </summary>
public sealed class BitNetServerHost : IHostedService, IAsyncDisposable
{
    private readonly BitNetOptions _options;
    private readonly ILogger<BitNetServerHost> _logger;

    // Per-tier process bookkeeping
    private readonly ConcurrentDictionary<ModelTier, ManagedServer> _servers = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _disposed;

    public BitNetServerHost(BitNetOptions options, ILogger<BitNetServerHost> logger)
    {
        _options = options;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // IHostedService
    // -------------------------------------------------------------------------

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("BitNetServerHost starting — launching eager tiers.");

        var eagarTasks = _options.Tiers
            .Where(kv => !kv.Value.LazySpawn)
            .Select(kv => EnsureStartedAsync(kv.Key, cancellationToken));

        await Task.WhenAll(eagarTasks).ConfigureAwait(false);
        _logger.LogInformation("BitNetServerHost: all eager tiers launched.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("BitNetServerHost stopping — killing all child processes.");
        await StopAllAsync().ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Ensures <paramref name="tier"/> is running. Safe to call concurrently.
    /// Returns immediately if the tier is already up.
    /// </summary>
    public async Task EnsureStartedAsync(ModelTier tier, CancellationToken ct = default)
    {
        if (_servers.TryGetValue(tier, out var existing) && existing.IsAlive)
            return;

        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_servers.TryGetValue(tier, out existing) && existing.IsAlive)
                return;

            await LaunchAsync(tier, ct).ConfigureAwait(false);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Returns true if the server process for <paramref name="tier"/> is currently alive.</summary>
    public bool IsRunning(ModelTier tier) =>
        _servers.TryGetValue(tier, out var s) && s.IsAlive;

    /// <summary>Port the tier is (or will be) listening on.</summary>
    public int GetPort(ModelTier tier)
    {
        if (_options.Tiers.TryGetValue(tier, out var cfg))
            return cfg.Port;
        throw new InvalidOperationException($"Tier {tier} has no configured port.");
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task LaunchAsync(ModelTier tier, CancellationToken ct)
    {
        if (!_options.Tiers.TryGetValue(tier, out var cfg))
            throw new InvalidOperationException($"No configuration found for tier {tier}.");

        if (!File.Exists(cfg.ModelPath))
            throw new InvalidOperationException(
                $"Model file for tier {tier} not found: {cfg.ModelPath}");

        if (!File.Exists(_options.ServerExePath))
            throw new InvalidOperationException(
                $"llama-server.exe not found at: {_options.ServerExePath}");

        var args = string.Join(" ",
            $"-m \"{cfg.ModelPath}\"",
            $"--host {_options.ServerHost}",
            $"--port {cfg.Port}",
            $"-t {_options.DefaultThreads}",
            $"-c {_options.DefaultContextSize}");

        _logger.LogInformation("Starting llama-server for {Tier} on port {Port}: {Args}",
            tier, cfg.Port, args);

        var psi = new ProcessStartInfo(_options.ServerExePath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var managed = new ManagedServer(tier, process, cfg.Port);

        process.Exited += (_, _) =>
        {
            _logger.LogWarning("llama-server for {Tier} exited unexpectedly (code {Code}).",
                tier, process.ExitCode);
            _servers.TryRemove(tier, out _);
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogDebug("[llama-server/{Tier}] {Line}", tier, e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogDebug("[llama-server/{Tier}/err] {Line}", tier, e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _servers[tier] = managed;

        // Wait until the HTTP endpoint responds (simple poll)
        await WaitForReadyAsync(tier, cfg.Port, ct).ConfigureAwait(false);
    }

    private async Task WaitForReadyAsync(ModelTier tier, int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var url = $"http://{_options.ServerHost}:{port}/health";
        var deadline = DateTime.UtcNow + _options.ServerStartupTimeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("llama-server for {Tier} is ready on port {Port}.", tier, port);
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not ready yet — keep polling
            }

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        _logger.LogWarning("llama-server for {Tier} did not become ready within {Timeout}.",
            tier, _options.ServerStartupTimeout);
    }

    private async Task StopAllAsync()
    {
        var tasks = _servers.Values.Select(s => s.StopAsync()).ToList();
        _servers.Clear();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // IAsyncDisposable
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAllAsync().ConfigureAwait(false);
        _startLock.Dispose();
    }

    // -------------------------------------------------------------------------
    // Inner type
    // -------------------------------------------------------------------------

    private sealed class ManagedServer(ModelTier tier, Process process, int port)
    {
        public ModelTier Tier { get; } = tier;
        public int Port { get; } = port;

        public bool IsAlive
        {
            get
            {
                try { return !process.HasExited; }
                catch { return false; }
            }
        }

        public async Task StopAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch { /* ignore — process may already be gone */ }
            finally
            {
                process.Dispose();
            }
        }
    }
}
