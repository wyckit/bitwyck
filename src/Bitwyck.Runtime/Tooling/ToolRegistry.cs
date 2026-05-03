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
    /// Single-line manifest (just `name|schema` per tool) so the system prompt
    /// stays under the small-context BitNet kernel threshold.
    /// </summary>
    public string ToPromptManifest()
    {
        if (_tools.IsEmpty) return "(no tools)";
        return string.Join(", ", _tools.Values.OrderBy(t => t.Name).Select(t => $"{t.Name}|{t.ArgumentSchema}"));
    }
}
