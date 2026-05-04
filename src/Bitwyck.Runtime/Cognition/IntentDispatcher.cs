using System.Text.RegularExpressions;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bitwyck.Runtime.Cognition;

/// <summary>
/// Pre-LLM short-circuit. Inspects a trigger payload and decides whether a
/// tool can be invoked directly without spending an LLM turn — by:
///   1. matching well-known intent patterns (regex / keyword), or
///   2. recalling a high-similarity past <c>decision precedent</c> from engram
///      (a previous successful tool call for a similar trigger).
///
/// Returns a <see cref="ToolCall"/> on hit, <c>null</c> on miss. The caller
/// (typically <c>AgentCommand</c>) executes the call and appends the result
/// to its working transcript before falling through to the LLM.
/// </summary>
public sealed class IntentDispatcher
{
    public const string PrecedentNamespace = "bitwyck-precedent";

    private readonly IToolRegistry _registry;
    private readonly IEngramMemoryStore? _engram;
    private readonly ILogger<IntentDispatcher>? _logger;

    public IntentDispatcher(
        IToolRegistry registry,
        IEngramMemoryStore? engram = null,
        ILogger<IntentDispatcher>? logger = null)
    {
        _registry = registry;
        _engram = engram;
        _logger = logger;
    }

    public async Task<DispatchResult> TryDispatchAsync(string trigger, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trigger)) return DispatchResult.Miss();

        // 1. Pattern-based shortcuts.
        var pattern = MatchPattern(trigger);
        if (pattern is not null && _registry.TryGet(pattern.ToolName, out var tool) && tool is not null)
        {
            _logger?.LogDebug("Intent shortcut: pattern '{Pattern}' -> {Tool}", pattern.RawText, pattern.ToolName);
            return DispatchResult.Hit(pattern, "pattern", confidence: 0.95);
        }

        // 2. Engram precedent recall.
        if (_engram is not null)
        {
            var matches = await _engram.SearchAsync(
                new EngramQuery(trigger, PrecedentNamespace, K: 3, Hybrid: true, MinScore: 0.55),
                ct);

            foreach (var m in matches)
            {
                var parsed = TryParsePrecedent(m);
                if (parsed is not null && _registry.TryGet(parsed.ToolName, out var t2) && t2 is not null)
                {
                    _logger?.LogDebug("Intent precedent: '{Source}' -> {Tool} (score={Score:F2})",
                        m.Text, parsed.ToolName, m.Score);
                    return DispatchResult.Hit(parsed, "precedent", confidence: m.Score);
                }
            }
        }

        return DispatchResult.Miss();
    }

    /// <summary>
    /// Records a successful tool call for future precedent recall.
    /// Called by <c>AgentCommand</c> after a tool invoked via the dispatcher
    /// (or by the LLM with no parsing repair) succeeds.
    /// </summary>
    public Task RecordSuccessAsync(string trigger, ToolCall call, CancellationToken ct = default)
    {
        if (_engram is null) return Task.CompletedTask;
        var id = $"prec-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{ShortHash(trigger + call.ToolName)}";
        var text = $"trigger: {Truncate(trigger, 200)}\ntool: {call.ToolName}\nargs: {string.Join(" | ", call.Arguments)}";
        var engram = new Engram(
            Id: id,
            Namespace: PrecedentNamespace,
            Text: text,
            Category: "decision-precedent",
            Lifecycle: EngramLifecycle.Stm,
            Timestamp: DateTimeOffset.UtcNow,
            Metadata: new Dictionary<string, string>
            {
                ["tool"] = call.ToolName,
                ["argCount"] = call.Arguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        return _engram.StoreAsync(engram, ct);
    }

    // -------------------------------------------------------------------------
    // Pattern matchers — high-precision, low-recall.
    // -------------------------------------------------------------------------

    private static readonly (string Source, Regex Rx, string ToolName, int[] ArgGroups)[] Patterns = new[]
    {
        // list_files
        ("list-files-in", new Regex(@"\b(?:list|show|enumerate)\s+(?:files|contents|entries)\s+(?:in|at|under|of)\s+(\S+)", RegexOptions.IgnoreCase), "list_files", new[]{1}),
        ("ls-path",       new Regex(@"^\s*ls\s+(\S+)\s*$", RegexOptions.IgnoreCase), "list_files", new[]{1}),

        // read_file
        ("read-file",   new Regex(@"\bread\s+(?:the\s+)?file\s+(\S+)", RegexOptions.IgnoreCase), "read_file", new[]{1}),
        ("show-file",   new Regex(@"\b(?:show|display|cat|print)\s+(?:me\s+)?(?:the\s+contents?\s+of\s+)?(?:file\s+)?(\S+\.\w+)\b", RegexOptions.IgnoreCase), "read_file", new[]{1}),
        ("whats-in",    new Regex(@"\bwhat(?:'s| is)\s+in\s+(\S+\.\w+)\b", RegexOptions.IgnoreCase), "read_file", new[]{1}),

        // write_file (low-confidence — keep behind explicit phrasing)
        ("write-file",  new Regex(@"\bwrite\s+(?:to\s+)?(\S+)\s+(?:with|the\s+content)\s+(.+)$", RegexOptions.IgnoreCase), "write_file", new[]{1, 2}),

        // run_bash
        ("run-cmd",     new Regex(@"^\s*run\s+(.+)$", RegexOptions.IgnoreCase), "run_bash", new[]{1}),
        ("execute",     new Regex(@"^\s*execute\s+(.+)$", RegexOptions.IgnoreCase), "run_bash", new[]{1}),

        // engram tools
        ("recall",      new Regex(@"\b(?:recall|remember|what\s+do\s+(?:we|I)\s+know\s+about)\s+(.+)$", RegexOptions.IgnoreCase), "query_engram", new[]{1}),
        ("query-mem",   new Regex(@"\b(?:query|search)\s+(?:engram|memory)\s+(?:for\s+)?(.+)$", RegexOptions.IgnoreCase), "query_engram", new[]{1}),

        // fetch_url — phrasing variants for asking the agent to read a web page
        ("read-link",   new Regex(@"\b(?:read|open|load|fetch|summari[sz]e|browse)\s+(?:this\s+|the\s+)?(?:link|url|page|article|webpage)?\s*[:\s-]*\s*(https?://\S+)", RegexOptions.IgnoreCase), "fetch_url", new[]{1}),
        ("whats-at-url",new Regex(@"\bwhat(?:'s|\s+is|\s+does)\s+(?:in\s+|at\s+|on\s+)?(https?://\S+)", RegexOptions.IgnoreCase), "fetch_url", new[]{1}),
        ("url-only",    new Regex(@"^\s*(https?://\S+)\s*$", RegexOptions.IgnoreCase), "fetch_url", new[]{1}),
    };

    public static ToolCall? MatchPattern(string trigger)
    {
        foreach (var (source, rx, toolName, groups) in Patterns)
        {
            var m = rx.Match(trigger);
            if (!m.Success) continue;
            var args = groups.Select(g => m.Groups[g].Value.Trim().Trim('"', '\'')).ToArray();
            return new ToolCall(toolName, args, m.Value);
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Precedent parsing
    // -------------------------------------------------------------------------

    private static ToolCall? TryParsePrecedent(Engram e)
    {
        // Format we wrote in RecordSuccessAsync:
        //   trigger: <text>\ntool: <name>\nargs: a | b | c
        var lines = e.Text.Split('\n');
        string? toolName = null;
        var args = Array.Empty<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
                toolName = line["tool:".Length..].Trim();
            else if (line.StartsWith("args:", StringComparison.OrdinalIgnoreCase))
                args = line["args:".Length..].Split('|').Select(s => s.Trim()).ToArray();
        }
        return string.IsNullOrEmpty(toolName) ? null : new ToolCall(toolName, args, e.Text);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string ShortHash(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

public sealed record DispatchResult(bool IsHit, ToolCall? Call, string Source, double Confidence)
{
    public static DispatchResult Hit(ToolCall call, string source, double confidence)
        => new(true, call, source, confidence);
    public static DispatchResult Miss() => new(false, null, "miss", 0.0);
}
