using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// Tool: <c>list_files|&lt;dir&gt;</c> or <c>list_files|&lt;dir&gt;|&lt;glob&gt;</c>
/// Lists files in a directory matching an optional glob pattern.
/// Returns up to 200 entries, one per line.
/// Rejects paths outside the configured allow-list roots.
/// </summary>
public sealed class ListFilesTool : ITool
{
    private const int MaxEntries = 200;

    private readonly IReadOnlyList<string> _allowedRoots;

    /// <param name="allowedRoots">
    /// Normalised root paths. Any path not beginning with one of these (case-insensitive)
    /// is rejected. Pass an empty list to reject all paths.
    /// </param>
    public ListFilesTool(IReadOnlyList<string> allowedRoots)
    {
        _allowedRoots = allowedRoots
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList()
            .AsReadOnly();
    }

    public string Name           => "list_files";
    public string Description    => "Lists files in a directory, optionally filtered by a glob pattern. Returns up to 200 entries.";
    public string ArgumentSchema => "dir|glob?";

    public Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (arguments.Count < 1 || string.IsNullOrWhiteSpace(arguments[0]))
            return Task.FromResult(ToolResult.Fail(Name, "Missing required argument: <dir>."));

        var rawDir = arguments[0].Trim();
        var glob   = arguments.Count >= 2 && !string.IsNullOrWhiteSpace(arguments[1])
            ? arguments[1].Trim()
            : "*";

        if (!TryValidatePath(rawDir, out var fullDir, out var error))
            return Task.FromResult(ToolResult.Fail(Name, error!));

        return ListCoreAsync(fullDir!, glob, ct);
    }

    private Task<ToolResult> ListCoreAsync(string fullDir, string glob, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(fullDir))
                return Task.FromResult(ToolResult.Fail(Name, $"Directory not found: {fullDir}"));

            var entries = Directory
                .EnumerateFileSystemEntries(fullDir, glob, SearchOption.AllDirectories)
                .Take(MaxEntries + 1)
                .ToList();

            bool truncated = entries.Count > MaxEntries;
            if (truncated)
                entries = entries.Take(MaxEntries).ToList();

            if (entries.Count == 0)
                return Task.FromResult(ToolResult.Ok(Name, "(no entries found)"));

            var lines = string.Join('\n', entries);
            if (truncated)
                lines += $"\n... [truncated at {MaxEntries} entries]";

            return Task.FromResult(ToolResult.Ok(Name, lines));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(ToolResult.Fail(Name, "Operation cancelled."));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolResult.Fail(Name, $"Access denied: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(Name, $"Error listing files: {ex.Message}"));
        }
    }

    private bool TryValidatePath(string rawPath, out string? fullPath, out string? error)
    {
        fullPath = null;
        error    = null;

        try
        {
            fullPath = Path.GetFullPath(rawPath);
        }
        catch (Exception ex)
        {
            error = $"Invalid path '{rawPath}': {ex.Message}";
            return false;
        }

        if (_allowedRoots.Count == 0)
        {
            error = "No allowed roots configured; all file access is denied.";
            return false;
        }

        foreach (var root in _allowedRoots)
        {
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                (fullPath.Length == root.Length || fullPath[root.Length] == Path.DirectorySeparatorChar || fullPath[root.Length] == Path.AltDirectorySeparatorChar))
            {
                return true;
            }
        }

        error = $"Access denied: '{fullPath}' is outside the allowed roots.";
        return false;
    }
}
