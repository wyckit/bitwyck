namespace Bitwyck.Core.Models;

/// <summary>
/// A parsed tool invocation extracted from an LLM token stream.
/// XML form: <![CDATA[<call>tool_name|arg1|arg2|...</call>]]>
/// </summary>
public sealed record ToolCall(
    string ToolName,
    IReadOnlyList<string> Arguments,
    string RawText
)
{
    public string ArgsJoined => string.Join("|", Arguments);
}

/// <summary>
/// Result of executing a tool. Always serializable as a string for injection
/// back into the LLM context as <![CDATA[<observation>...</observation>]]>.
/// </summary>
public sealed record ToolResult(
    string ToolName,
    bool Success,
    string Output,
    string? Error = null,
    TimeSpan? Duration = null
)
{
    public static ToolResult Ok(string toolName, string output, TimeSpan? duration = null) =>
        new(toolName, true, output, null, duration);

    public static ToolResult Fail(string toolName, string error, TimeSpan? duration = null) =>
        new(toolName, false, string.Empty, error, duration);

    public string ToObservation()
    {
        if (Success)
            return $"<observation>{Output}</observation>";
        return $"<observation error=\"true\">{Error}</observation>";
    }
}
