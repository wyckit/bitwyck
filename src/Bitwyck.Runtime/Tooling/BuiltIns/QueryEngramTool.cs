using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// <c>query_engram|&lt;text&gt;[|&lt;namespace&gt;[|&lt;k&gt;]]</c>
/// Searches the engram store and returns scored entries.
/// </summary>
public sealed class QueryEngramTool : ITool
{
    private readonly IEngramMemoryStore _store;
    private readonly string _defaultNamespace;

    public QueryEngramTool(IEngramMemoryStore store, string defaultNamespace = "bitwyck-episodic")
    {
        _store = store;
        _defaultNamespace = defaultNamespace;
    }

    public string Name => "query_engram";
    public string Description => "Search engram memory for relevant past entries.";
    public string ArgumentSchema => "text|namespace?|k?";

    public async Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (arguments.Count < 1 || string.IsNullOrWhiteSpace(arguments[0]))
            return ToolResult.Fail(Name, "missing query text");

        var text = arguments[0];
        var ns = arguments.Count >= 2 && !string.IsNullOrWhiteSpace(arguments[1]) ? arguments[1] : _defaultNamespace;
        var k = 5;
        if (arguments.Count >= 3 && int.TryParse(arguments[2], out var parsed))
            k = Math.Clamp(parsed, 1, 20);

        try
        {
            var results = await _store.SearchAsync(new EngramQuery(text, ns, k, Hybrid: true, ExpandGraph: false), ct);
            if (results.Count == 0) return ToolResult.Ok(Name, "[no matches]");
            var sb = new System.Text.StringBuilder();
            foreach (var r in results)
                sb.Append('[').Append(r.Score.ToString("F2")).Append("] ").Append(r.Id).Append(": ").AppendLine(r.Text.Length > 200 ? r.Text[..200] + "..." : r.Text);
            return ToolResult.Ok(Name, sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(Name, ex.Message);
        }
    }
}
