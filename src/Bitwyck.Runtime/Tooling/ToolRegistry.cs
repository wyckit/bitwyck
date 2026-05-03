using System.Collections.Concurrent;
using System.Text;
using Bitwyck.Core.Interfaces;

namespace Bitwyck.Runtime.Tooling;

/// <summary>
/// Thread-safe registry of <see cref="ITool"/> instances.
/// Implements <see cref="IToolRegistry"/> for DI registration.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
    }

    public bool TryGet(string name, out ITool? tool)
    {
        var found = _tools.TryGetValue(name, out var t);
        tool = t;
        return found;
    }

    public IReadOnlyCollection<ITool> All() => _tools.Values.ToList().AsReadOnly();

    /// <summary>
    /// Compact one-line-per-tool manifest. The LLM sees `name|schema — description`
    /// for each tool. Compact form keeps the system prompt small enough for
    /// small-context BitNet kernels.
    /// </summary>
    public string ToPromptManifest()
    {
        if (_tools.IsEmpty) return "(no tools)";
        var sb = new StringBuilder();
        foreach (var tool in _tools.Values.OrderBy(t => t.Name))
            sb.Append(tool.Name).Append('|').Append(tool.ArgumentSchema).Append(" - ").AppendLine(tool.Description);
        return sb.ToString().TrimEnd();
    }
}
