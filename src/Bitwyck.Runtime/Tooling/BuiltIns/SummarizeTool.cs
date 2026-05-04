using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Inference;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// <c>summarize|&lt;input-or-@file&gt;[|chunkChars[|maxOutputChars]]</c>
/// Map-reduce summarization for inputs longer than the model's context window.
/// Input may be inline text or <c>@</c>-prefixed file path. Splits on paragraph
/// boundaries when possible, runs each chunk through the LLM with a "summarize"
/// prompt, then reduces the chunk summaries into a single cohesive summary.
/// Lets the agent absorb arbitrarily long content (articles, files, fetched
/// pages) without ever exceeding the per-call prompt limit.
/// </summary>
public sealed class SummarizeTool : ITool
{
    private readonly IBitNetInferenceClient _llm;
    private readonly int _chunkChars;
    private readonly ModelTier _tier;

    public SummarizeTool(IBitNetInferenceClient llm, BitNetOptions opts, ModelTier tier = ModelTier.Reflex_1B)
    {
        _llm = llm;
        _tier = tier;
        // Leave headroom in the model's prompt envelope for the
        // "Summarize the following:" framing + completion buffer.
        _chunkChars = Math.Max(2000, opts.MaxPromptChars - 1500);
    }

    public string Name => "summarize";
    public string Description => "Map-reduce summarize long text or a file (use @path). Returns a cohesive summary.";
    public string ArgumentSchema => "input|chunkChars?|maxOutputChars?";

    public async Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (arguments.Count < 1 || string.IsNullOrWhiteSpace(arguments[0]))
            return ToolResult.Fail(Name, "missing input (text or @path/to/file)");

        string input;
        try { input = await ResolveInputAsync(arguments[0], ct); }
        catch (Exception ex) { return ToolResult.Fail(Name, $"failed to load input: {ex.Message}"); }

        if (string.IsNullOrWhiteSpace(input))
            return ToolResult.Fail(Name, "input is empty");

        var chunkSize = arguments.Count >= 2 && int.TryParse(arguments[1], out var cs) && cs > 500
            ? Math.Min(cs, _chunkChars) : _chunkChars;
        var maxOutput = arguments.Count >= 3 && int.TryParse(arguments[2], out var mo) && mo > 100
            ? mo : 4000;

        var chunks = ChunkText(input, chunkSize);
        if (chunks.Count == 0) return ToolResult.Fail(Name, "no content to summarize");

        // MAP: summarize each chunk in parallel-ish (sequential to avoid CPU thrash).
        var summaries = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var prompt = $"Summarize the following text in 2-3 short sentences. Output only the summary, no preamble.\n\n{chunks[i]}";
            try
            {
                var resp = await _llm.CompleteAsync(new InferenceRequest(
                    Tier: _tier,
                    Messages: new[]
                    {
                        new InferenceMessage(MessageRole.System, "You are a concise summarizer. Output only the requested summary."),
                        new InferenceMessage(MessageRole.User, prompt),
                    },
                    MaxTokens: 250,
                    Temperature: 0.2), ct);
                var s = resp.Content.Trim();
                if (s.Length > 0) summaries.Add(s);
            }
            catch (Exception ex)
            {
                summaries.Add($"[chunk {i + 1} failed: {ex.Message}]");
            }
        }

        if (summaries.Count == 0) return ToolResult.Fail(Name, "all chunk summaries failed");
        if (summaries.Count == 1) return ToolResult.Ok(Name, Truncate(summaries[0], maxOutput));

        // REDUCE: combine chunk summaries into one cohesive summary.
        var combined = string.Join("\n\n", summaries.Select((s, i) => $"[part {i + 1}] {s}"));

        if (combined.Length <= maxOutput)
            return ToolResult.Ok(Name, $"({summaries.Count} chunks)\n\n{combined}");

        try
        {
            var reducePrompt = $"The following are summaries of consecutive sections of one document. Combine them into one cohesive summary of 5-8 sentences. Preserve key facts and chronology.\n\n{combined}";
            var resp = await _llm.CompleteAsync(new InferenceRequest(
                Tier: _tier,
                Messages: new[]
                {
                    new InferenceMessage(MessageRole.System, "You are a concise summarizer. Output only the requested summary."),
                    new InferenceMessage(MessageRole.User, reducePrompt),
                },
                MaxTokens: 600,
                Temperature: 0.2), ct);
            var reduced = Truncate(resp.Content.Trim(), maxOutput);
            return ToolResult.Ok(Name, $"({summaries.Count} chunks reduced)\n\n{reduced}");
        }
        catch (Exception ex)
        {
            // Reduce step failed — fall back to the concatenated chunk summaries.
            return ToolResult.Ok(Name, $"({summaries.Count} chunks; reduce failed: {ex.Message})\n\n{Truncate(combined, maxOutput)}");
        }
    }

    internal static async Task<string> ResolveInputAsync(string s, CancellationToken ct)
    {
        string raw;
        if (s.StartsWith('@'))
        {
            var path = s[1..].Trim();
            if (!File.Exists(path)) throw new FileNotFoundException($"file not found: {path}");
            raw = await File.ReadAllTextAsync(path, ct);
        }
        else { raw = s; }
        return SanitizeForBitNet(raw);
    }

    /// <summary>
    /// Replace characters that the 1.58-bit Falcon3 / BitNet kernels in this
    /// llama.cpp build choke on (em-dash, en-dash, smart quotes, NBSP, etc.).
    /// These were observed to cause STATUS_STACK_BUFFER_OVERRUN crashes inside
    /// the lookup-table matmul kernel even with the bumped 8 MB stack.
    /// </summary>
    internal static string SanitizeForBitNet(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '—' or '–' => '-',  // em-dash, en-dash
                '‘' or '’' or 'ʼ' => '\'',  // smart single quotes
                '“' or '”' => '"',  // smart double quotes
                '…' => "...",           // ellipsis (returns string, handled below)
                ' ' => ' ',             // non-breaking space
                '​' or '‌' or '‍' or '﻿' => string.Empty, // ZWSP, ZWNJ, ZWJ, BOM
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>Greedy text chunker that prefers paragraph (\\n\\n) and sentence boundaries.</summary>
    internal static IReadOnlyList<string> ChunkText(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        if (text.Length <= chunkSize) return new[] { text };

        var chunks = new List<string>();
        var pos = 0;
        while (pos < text.Length)
        {
            var remaining = text.Length - pos;
            if (remaining <= chunkSize) { chunks.Add(text[pos..]); break; }

            var end = pos + chunkSize;
            // Prefer a paragraph break inside the last 25 % of the window.
            var minBoundary = pos + (chunkSize * 3 / 4);
            var boundary = text.LastIndexOf("\n\n", end, end - minBoundary);
            if (boundary < 0) boundary = text.LastIndexOf(". ", end, end - minBoundary);
            if (boundary < 0) boundary = text.LastIndexOf(' ', end, end - minBoundary);
            if (boundary < 0) boundary = end; // hard cut

            chunks.Add(text[pos..boundary].Trim());
            pos = boundary;
            while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\n' || text[pos] == '\r')) pos++;
        }
        return chunks;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
