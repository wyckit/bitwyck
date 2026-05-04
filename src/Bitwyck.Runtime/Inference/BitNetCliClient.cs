using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bitwyck.Runtime.Inference;

/// <summary>
/// Implements <see cref="IBitNetInferenceClient"/> by spawning <c>llama-cli.exe</c>
/// as a child process. Intended as an offline fallback when the HTTP server is unavailable.
/// <para>
/// <see cref="StreamAsync"/> emits the full completion as a single <see cref="InferenceTokenChunk"/>
/// with <c>IsFinal = true</c> because the CLI does not natively stream.
/// </para>
/// </summary>
public sealed class BitNetCliClient : IBitNetInferenceClient
{
    private readonly BitNetOptions _options;
    private readonly ILogger<BitNetCliClient> _logger;

    public BitNetCliClient(BitNetOptions options, ILogger<BitNetCliClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // IBitNetInferenceClient
    // -------------------------------------------------------------------------

    public Task<bool> IsAvailableAsync(ModelTier tier, CancellationToken ct = default)
    {
        if (!_options.Tiers.TryGetValue(tier, out var cfg))
            return Task.FromResult(false);

        var available = File.Exists(_options.CliExePath) && File.Exists(cfg.ModelPath);
        return Task.FromResult(available);
    }

    /// <summary>
    /// Hard upper bound on prompt length. BitNet 1.58-bit kernels in this
    /// llama.cpp build stack-overflow on inputs above ~1500 chars / ~375 tokens.
    /// Configurable via <see cref="BitNetOptions.MaxPromptChars"/> (default 1400).
    /// </summary>
    private int PromptCharLimit => _options.MaxPromptChars > 0 ? _options.MaxPromptChars : 1400;

    public async Task<InferenceResponse> CompleteAsync(
        InferenceRequest request, CancellationToken ct = default)
    {
        var (prompt, modelPath) = PrepareInvocation(request);

        if (prompt.Length > PromptCharLimit)
        {
            _logger.LogWarning(
                "Prompt length {Len} exceeds safe limit {Limit} for {Tier}; skipping inference (would crash llama-cli).",
                prompt.Length, PromptCharLimit, request.Tier);
            throw new InvalidOperationException(
                $"prompt length {prompt.Length} exceeds safe limit {PromptCharLimit} for tier {request.Tier}");
        }

        var sw = Stopwatch.StartNew();
        var (stdout, _) = await RunCliAsync(modelPath, prompt, request, ct).ConfigureAwait(false);
        sw.Stop();

        // llama-cli echoes the prompt before the completion; strip it.
        var completion = StripPromptEcho(stdout, prompt);

        // Estimate token counts (rough: 1 token ≈ 4 chars)
        int promptTokens = prompt.Length / 4;
        int completionTokens = completion.Length / 4;

        return new InferenceResponse(
            Content: completion,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            Model: request.Tier.ToString(),
            Duration: sw.Elapsed,
            Truncated: completionTokens >= request.MaxTokens);
    }

    public async IAsyncEnumerable<InferenceTokenChunk> StreamAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // CLI does not stream; yield entire output as one final chunk.
        var response = await CompleteAsync(request, ct).ConfigureAwait(false);
        yield return new InferenceTokenChunk(response.Content, IsFinal: true);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private (string Prompt, string ModelPath) PrepareInvocation(InferenceRequest request)
    {
        if (!_options.Tiers.TryGetValue(request.Tier, out var cfg))
            throw new InvalidOperationException($"No configuration for tier {request.Tier}.");

        if (!File.Exists(cfg.ModelPath))
            throw new InvalidOperationException(
                $"Model file for tier {request.Tier} not found: {cfg.ModelPath}");

        if (!File.Exists(_options.CliExePath))
            throw new InvalidOperationException(
                $"llama-cli.exe not found at: {_options.CliExePath}");

        // Use ChatML chat template — required by Falcon3 / BitNet-2B-4T instruct models.
        // Plain "User:/Assistant:" format causes the model to emit EOS immediately.
        var sb = new StringBuilder();
        foreach (var msg in request.Messages)
        {
            var roleTag = msg.Role switch
            {
                MessageRole.System => "system",
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                MessageRole.Tool => "tool",
                _ => "user",
            };
            sb.Append("<|").Append(roleTag).Append("|>\n").Append(msg.Content).Append('\n');
        }
        sb.Append("<|assistant|>\n");

        return (sb.ToString(), cfg.ModelPath);
    }

    private async Task<(string Stdout, string Stderr)> RunCliAsync(
        string modelPath, string prompt, InferenceRequest request, CancellationToken ct)
    {
        int seed = request.Seed ?? 42;
        var ci = System.Globalization.CultureInfo.InvariantCulture;

        // Use ArgumentList — each entry is escaped/quoted per-argument by the runtime,
        // which correctly handles newlines and quotes inside the prompt. Concatenating
        // into Arguments string fails on Windows when the prompt has embedded newlines
        // (cmd-line parser truncates the prompt at the first newline → garbage tokens →
        // STATUS_STACK_BUFFER_OVERRUN inside llama-cli's tokenizer).
        var psi = new ProcessStartInfo(_options.CliExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(modelPath);
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("-n"); psi.ArgumentList.Add(request.MaxTokens.ToString(ci));
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(_options.DefaultThreads.ToString(ci));
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(_options.DefaultContextSize.ToString(ci));
        psi.ArgumentList.Add("--temp"); psi.ArgumentList.Add(request.Temperature.ToString("F2", ci));
        psi.ArgumentList.Add("--top-p"); psi.ArgumentList.Add(request.TopP.ToString("F2", ci));
        psi.ArgumentList.Add("-s"); psi.ArgumentList.Add(seed.ToString(ci));
        psi.ArgumentList.Add("--no-warmup");

        if (request.StopSequences is { Count: > 0 })
        {
            foreach (var stop in request.StopSequences)
            {
                psi.ArgumentList.Add("--stop");
                psi.ArgumentList.Add(stop);
            }
        }

        _logger.LogDebug("Invoking llama-cli with {ArgCount} arguments; prompt length {PromptLen}", psi.ArgumentList.Count, prompt.Length);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "bitwyck-last-prompt.txt"), prompt); } catch { }

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            // llama-cli's stderr is enormous (full model loading dump). Log only
            // the exit code and the last few lines at warning level; full stderr
            // is available at debug.
            var stderr = stderrBuilder.ToString();
            var tail = TailLines(stderr, 4);
            _logger.LogWarning("llama-cli exited with code {Code}. Tail: {Tail}", process.ExitCode, tail);
            _logger.LogDebug("llama-cli full stderr ({Len} chars): {Stderr}", stderr.Length, stderr);
            throw new InvalidOperationException($"llama-cli exited with code {process.ExitCode}");
        }

        return (stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    private static string TailLines(string s, int lines)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var split = s.Split('\n');
        var start = Math.Max(0, split.Length - lines);
        return string.Join(" | ", split[start..].Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    /// <summary>
    /// Removes the echoed prompt prefix from the raw CLI output.
    /// llama-cli outputs the full prompt followed by the generated completion.
    /// </summary>
    private static string StripPromptEcho(string stdout, string prompt)
    {
        var trimmed = stdout.TrimStart();
        var promptTrimmed = prompt.TrimStart();

        // Try exact prefix strip first
        if (trimmed.StartsWith(promptTrimmed, StringComparison.Ordinal))
            return CleanCompletion(trimmed[promptTrimmed.Length..]);

        // ChatML boundary (Falcon3 / BitNet-Instruct).
        const string chatmlBoundary = "<|assistant|>";
        var idx = trimmed.LastIndexOf(chatmlBoundary, StringComparison.Ordinal);
        if (idx >= 0)
            return CleanCompletion(trimmed[(idx + chatmlBoundary.Length)..]);

        // Legacy boundary.
        const string legacy = "Assistant:";
        idx = trimmed.LastIndexOf(legacy, StringComparison.Ordinal);
        if (idx >= 0)
            return CleanCompletion(trimmed[(idx + legacy.Length)..]);

        return trimmed.Trim();
    }

    private static string CleanCompletion(string s)
    {
        s = s.Trim();
        // llama-cli emits "[end of text]" as a sentinel after generation completes.
        var eot = s.LastIndexOf("[end of text]", StringComparison.Ordinal);
        if (eot >= 0) s = s[..eot].TrimEnd();
        return s;
    }
}
