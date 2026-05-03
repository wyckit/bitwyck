using Bitwyck.Core.Models;

namespace Bitwyck.Core.Interfaces;

public interface ITool
{
    /// <summary>Stable kebab/snake-case identifier the LLM emits in <![CDATA[<call>]]>.</summary>
    string Name { get; }

    /// <summary>Human-readable purpose for the system prompt.</summary>
    string Description { get; }

    /// <summary>Pipe-delimited argument schema for the system prompt (e.g. "path|content").</summary>
    string ArgumentSchema { get; }

    Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default);
}
