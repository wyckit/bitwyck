using System.Text.Json;
using System.Text.RegularExpressions;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Core.Utilities;

namespace Bitwyck.Runtime.Tooling;

/// <summary>
/// Multi-format tool-call extractor. Salvages tool calls from LLM output even
/// when the model didn't emit the canonical <c>&lt;call&gt;name|args&lt;/call&gt;</c>
/// syntax. Handles:
/// <list type="bullet">
///   <item><c>&lt;call&gt;name|arg|arg&lt;/call&gt;</c> — canonical (delegates to <see cref="XmlCallParser"/>).</item>
///   <item><c>[call]name|args[/call]</c> / <c>[[call]]name|args[[/call]]</c> — bracket variants.</item>
///   <item>JSON: <c>{"tool":"name","args":[...]}</c> or <c>{"name":"...","arguments":[...]}</c>.</item>
///   <item>Function-call: <c>name(arg1, arg2)</c> when <c>name</c> matches a registered tool.</item>
///   <item>Code-fence: triple-backtick blocks containing any of the above.</item>
/// </list>
/// After extraction, tool names are <b>fuzzy-matched</b> against the registry
/// (Levenshtein ≤ 2 OR substring match) so <c>list_file</c>, <c>listfiles</c>,
/// <c>LS</c> all snap to <c>list_files</c>.
/// </summary>
public static class ToolCallExtractor
{
    private static readonly Regex BracketCall =
        new(@"\[+\s*call\s*\]+\s*([^\[\]]+?)\s*\[+\s*/\s*call\s*\]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex JsonObject =
        new(@"\{[^{}]*?(?:""tool""|""name""|""function"")\s*:\s*""([^""]+)""[^{}]*\}",
            RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex CodeFence =
        new(@"```(?:\w+)?\s*\n?(.*?)\n?```", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex FunctionCall =
        new(@"\b([a-z][a-z0-9_]{2,})\s*\(\s*([^()]*?)\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<ToolCall> Extract(string llmOutput, IToolRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(llmOutput)) return Array.Empty<ToolCall>();

        var found = new List<ToolCall>();
        var toolNames = registry.All().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Canonical <call>...</call>
        found.AddRange(XmlCallParser.ParseAll(llmOutput));

        // 2. [call]...[/call] / [[call]]...[[/call]]
        foreach (Match m in BracketCall.Matches(llmOutput))
            if (XmlCallParser.TryParseInner(m.Groups[1].Value, m.Value, out var call))
                AddIfNew(found, call!);

        // 3. JSON {"tool":"name","args":[...]}
        var fenceContent = string.Join("\n", CodeFence.Matches(llmOutput).Select(m => m.Groups[1].Value));
        var jsonScanText = string.IsNullOrWhiteSpace(fenceContent) ? llmOutput : fenceContent;
        foreach (Match jm in JsonObject.Matches(jsonScanText))
        {
            if (TryParseJsonCall(jm.Value, out var jcall))
                AddIfNew(found, jcall!);
        }

        // 4. Function-call style: name(arg1, arg2). Only accept names that are
        //    registered tools (so we don't mis-trigger on ordinary prose like "say(hi)").
        foreach (Match fm in FunctionCall.Matches(llmOutput))
        {
            var name = fm.Groups[1].Value;
            if (!toolNames.Contains(name)) continue;
            var argsRaw = fm.Groups[2].Value;
            var args = ParseFunctionArgs(argsRaw);
            AddIfNew(found, new ToolCall(name, args, fm.Value));
        }

        // 5. Fuzzy-match every collected name against the registry.
        var snapped = found.Select(c => SnapToolName(c, toolNames)).Where(c => c is not null).Cast<ToolCall>().ToList();

        return Dedupe(snapped);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool TryParseJsonCall(string fragment, out ToolCall? call)
    {
        call = null;
        try
        {
            using var doc = JsonDocument.Parse(fragment);
            var root = doc.RootElement;
            string? name = null;
            if (root.TryGetProperty("tool", out var t)) name = t.GetString();
            else if (root.TryGetProperty("name", out var n)) name = n.GetString();
            else if (root.TryGetProperty("function", out var f)) name = f.GetString();
            if (string.IsNullOrEmpty(name)) return false;

            var args = new List<string>();
            if (root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                args.AddRange(a.EnumerateArray().Select(JsonValueToString));
            else if (root.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.Array)
                args.AddRange(ar.EnumerateArray().Select(JsonValueToString));
            else if (root.TryGetProperty("arguments", out var ar2) && ar2.ValueKind == JsonValueKind.Object)
                args.AddRange(ar2.EnumerateObject().Select(p => JsonValueToString(p.Value)));

            call = new ToolCall(name!, args, fragment);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string JsonValueToString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? string.Empty,
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText(),
    };

    private static IReadOnlyList<string> ParseFunctionArgs(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        // Handle both quoted ("a", "b") and bare (a, b) forms.
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        char quoteCh = '"';
        foreach (var c in raw)
        {
            if (inQuote)
            {
                if (c == quoteCh) { inQuote = false; continue; }
                current.Append(c);
            }
            else
            {
                if (c == ',') { parts.Add(current.ToString().Trim()); current.Clear(); }
                else if (c == '"' || c == '\'') { inQuote = true; quoteCh = c; }
                else current.Append(c);
            }
        }
        if (current.Length > 0) parts.Add(current.ToString().Trim());
        return parts.Where(p => p.Length > 0).ToArray();
    }

    private static ToolCall? SnapToolName(ToolCall call, HashSet<string> toolNames)
    {
        if (toolNames.Contains(call.ToolName)) return call;

        var match = toolNames
            .Select(n => (Name: n, Score: SimilarityScore(n, call.ToolName)))
            .Where(x => x.Score >= 0.65)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
        return match.Name is null ? null : call with { ToolName = match.Name };
    }

    private static double SimilarityScore(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var ai = a.ToLowerInvariant();
        var bi = b.ToLowerInvariant();
        if (ai == bi) return 1.0;
        if (ai.Contains(bi) || bi.Contains(ai)) return 0.85;
        var dist = Levenshtein(ai, bi);
        var maxLen = Math.Max(ai.Length, bi.Length);
        return 1.0 - (double)dist / maxLen;
    }

    private static int Levenshtein(string s, string t)
    {
        if (s.Length == 0) return t.Length;
        if (t.Length == 0) return s.Length;
        var d = new int[s.Length + 1, t.Length + 1];
        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;
        for (var i = 1; i <= s.Length; i++)
        for (var j = 1; j <= t.Length; j++)
        {
            var cost = s[i - 1] == t[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
        }
        return d[s.Length, t.Length];
    }

    private static void AddIfNew(List<ToolCall> list, ToolCall call)
    {
        if (!list.Any(c => c.ToolName == call.ToolName && c.ArgsJoined == call.ArgsJoined))
            list.Add(call);
    }

    private static IReadOnlyList<ToolCall> Dedupe(List<ToolCall> calls)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<ToolCall>();
        foreach (var c in calls)
        {
            var key = $"{c.ToolName}|{c.ArgsJoined}";
            if (seen.Add(key)) output.Add(c);
        }
        return output;
    }
}
