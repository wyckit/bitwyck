using System.Text;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// Tool: <c>write_file|&lt;path&gt;|&lt;content&gt;</c>
/// Writes UTF-8 content to a file, creating parent directories as needed.
/// Rejects paths outside the configured allow-list roots.
/// </summary>
public sealed class WriteFileTool : ITool
{
    private readonly IReadOnlyList<string> _allowedRoots;

    /// <param name="allowedRoots">
    /// Normalised root paths. Any path not beginning with one of these (case-insensitive)
    /// is rejected. Pass an empty list to reject all paths.
    /// </param>
    public WriteFileTool(IReadOnlyList<string> allowedRoots)
    {
        _allowedRoots = allowedRoots
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList()
            .AsReadOnly();
    }

    public string Name           => "write_file";
    public string Description    => "Writes UTF-8 content to a file, creating parent directories if needed.";
    public string ArgumentSchema => "path|content";

    public Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (arguments.Count < 1 || string.IsNullOrWhiteSpace(arguments[0]))
            return Task.FromResult(ToolResult.Fail(Name, "Missing required argument: <path>."));

        var rawPath = arguments[0].Trim();
        var content = arguments.Count >= 2 ? arguments[1] : string.Empty;

        if (!TryValidatePath(rawPath, out var fullPath, out var error))
            return Task.FromResult(ToolResult.Fail(Name, error!));

        return WriteCoreAsync(fullPath!, content, ct);
    }

    private async Task<ToolResult> WriteCoreAsync(string fullPath, string content, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var bytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(fullPath, bytes, ct).ConfigureAwait(false);

            return ToolResult.Ok(Name, $"wrote {bytes.Length} bytes to {fullPath}");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail(Name, "Operation cancelled.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Fail(Name, $"Access denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(Name, $"Error writing file: {ex.Message}");
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
