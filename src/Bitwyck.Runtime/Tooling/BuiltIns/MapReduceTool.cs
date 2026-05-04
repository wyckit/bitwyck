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
    private readonly ModelTier _tier;

    public MapReduceTool(IBitNetInferenceClient llm, BitNetOptions opts, ModelTier tier = ModelTier.Reflex_1B)
    {
        _llm = llm;
        _tier = tier;
        _chunkChars = Math.Max(2000, opts.MaxPromptChars - 1500);
    }

    public string Name => "map_reduce";
    public string Description => "Apply an instruction to each chunk of a long input (text or @path) and concatenate results.";
    public string ArgumentSchema => "instruction|input|chunkChars?";

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
                    Tier: _tier,
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
