using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Inference;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// <c>map_reduce|&lt;instruction&gt;|&lt;input-or-@file&gt;[|chunkChars]</c>
/// Generic chunked-prompt runner. For each chunk of the input, runs the
/// instruction against the LLM. Returns the per-chunk outputs concatenated,
/// then optionally a final combined pass if asked. Use cases: extracting
/// names from a long document, classifying paragraphs, translating large
/// texts, finding all occurrences of X — anything where the operation can
/// be applied independently per chunk.
/// </summary>
public sealed class MapReduceTool : ITool
{
    private readonly IBitNetInferenceClient _llm;
    private readonly int _chunkChars;
    private readonly ModelTier _shallowTier;
    private readonly ModelTier _deepTier;

    public MapReduceTool(IBitNetInferenceClient llm, BitNetOptions opts,
        ModelTier shallowTier = ModelTier.Reflex_1B,
        ModelTier? deepTier = null)
    {
        _llm = llm;
        _shallowTier = shallowTier;
        _deepTier = deepTier ?? opts.DeepTier;
        _chunkChars = Math.Max(2000, opts.MaxPromptChars - 1500);
    }

    public string Name => "map_reduce";
    public string Description => "Apply an instruction to each chunk of a long input (text or @path) and concatenate results. Pass 'deep' as last arg for higher-quality tier.";
    public string ArgumentSchema => "instruction|input|chunkChars?|mode?";

    public async Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (arguments.Count < 2)
            return ToolResult.Fail(Name, "expected: instruction|input[|chunkChars]");

        var instruction = arguments[0].Trim();
        if (string.IsNullOrEmpty(instruction))
            return ToolResult.Fail(Name, "instruction is required");

        string input;
        try { input = await SummarizeTool.ResolveInputAsync(arguments[1], ct); }
        catch (Exception ex) { return ToolResult.Fail(Name, $"failed to load input: {ex.Message}"); }

        if (string.IsNullOrWhiteSpace(input)) return ToolResult.Fail(Name, "input is empty");

        var chunkSize = arguments.Count >= 3 && int.TryParse(arguments[2], out var cs) && cs > 500
            ? Math.Min(cs, _chunkChars) : _chunkChars;

        // mode: "deep" → run on the deep tier (slower, better); else fast tier.
        var mode = arguments.Count >= 4 ? arguments[3].Trim().ToLowerInvariant() : "default";
        var tier = mode == "deep" ? _deepTier : _shallowTier;

        var chunks = SummarizeTool.ChunkText(input, chunkSize);
        if (chunks.Count == 0) return ToolResult.Fail(Name, "no content");

        var outputs = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var prompt = $"{instruction}\n\nInput chunk ({i + 1}/{chunks.Count}):\n{chunks[i]}";
            try
            {
                var resp = await _llm.CompleteAsync(new InferenceRequest(
                    Tier: tier,
                    Messages: new[]
                    {
                        new InferenceMessage(MessageRole.System, "Apply the user's instruction to the input chunk. Output only the result, no preamble."),
                        new InferenceMessage(MessageRole.User, prompt),
                    },
                    MaxTokens: 400,
                    Temperature: 0.2), ct);
                outputs.Add($"[chunk {i + 1}/{chunks.Count}]\n{resp.Content.Trim()}");
            }
            catch (Exception ex)
            {
                outputs.Add($"[chunk {i + 1}/{chunks.Count} failed: {ex.Message}]");
            }
        }

        return ToolResult.Ok(Name, string.Join("\n\n", outputs));
    }
}
