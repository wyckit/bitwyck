using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// <c>store_engram|&lt;id&gt;|&lt;namespace&gt;|&lt;text&gt;[|&lt;category&gt;]</c>
/// Persists a new entry into the engram brain.
/// </summary>
public sealed class StoreEngramTool : ITool
{
    private readonly IEngramMemoryStore _store;

    public StoreEngramTool(IEngramMemoryStore store) { _store = store; }

    public string Name => "store_engram";
    public string Description => "Write a new memory entry into the engram brain.";
    public string ArgumentSchema => "id|namespace|text|category?";

    public async Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (arguments.Count < 3) return ToolResult.Fail(Name, "expected: id|namespace|text[|category]");
        var id = arguments[0];
        var ns = arguments[1];
        var text = arguments[2];
        var category = arguments.Count >= 4 ? arguments[3] : null;

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(text))
            return ToolResult.Fail(Name, "id, namespace, and text are all required");

        try
        {
            var engram = new Engram(
                Id: id,
                Namespace: ns,
                Text: text,
                Category: category,
                Lifecycle: EngramLifecycle.Stm,
                Timestamp: DateTimeOffset.UtcNow);

            await _store.StoreAsync(engram, ct);
            return ToolResult.Ok(Name, $"stored {id} in {ns}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(Name, ex.Message);
        }
    }
}
