using System.Text.RegularExpressions;
using Bitwyck.Core.Models;

namespace Bitwyck.Core.Utilities;

/// <summary>
/// Parses tool-call XML emitted by the LLM. Used in two modes:
///   1. Whole-string scan: ParseAll(text) returns every complete <call>...</call>.
///   2. Streaming: see XmlToolInterceptor (in Bitwyck.Runtime) for the state machine.
///
/// Argument syntax: pipe-delimited. The first segment is the tool name.
///   <call>read_file|C:/foo/bar.txt</call>
///   <call>spawn_agent|summarize this paragraph|extra arg</call>
/// </summary>
public static class XmlCallParser
{
    private static readonly Regex CallRegex = new(
        @"<call>([^<]*?)</call>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static IReadOnlyList<ToolCall> ParseAll(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<ToolCall>();
        var results = new List<ToolCall>();
        foreach (Match m in CallRegex.Matches(text))
        {
            if (TryParseInner(m.Groups[1].Value, m.Value, out var call))
                results.Add(call!);
        }
        return results;
    }

    public static bool TryParseInner(string innerText, string rawText, out ToolCall? call)
    {
        call = null;
        if (string.IsNullOrWhiteSpace(innerText)) return false;
        var parts = innerText.Split('|');
        if (parts.Length == 0) return false;
        var name = parts[0].Trim();
        if (name.Length == 0) return false;
        var args = parts.Length == 1
            ? Array.Empty<string>()
            : parts[1..].Select(a => a.Trim()).ToArray();
        call = new ToolCall(name, args, rawText);
        return true;
    }
}
