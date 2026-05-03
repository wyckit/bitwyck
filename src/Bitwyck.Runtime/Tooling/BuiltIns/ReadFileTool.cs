using System.Text;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// Tool: <c>read_file|&lt;path&gt;</c>
/// Returns the UTF-8 contents of the specified file.
/// Rejects paths outside the configured allow-list roots.
/// Truncates output to 8 KB and appends "... [truncated]" if larger.
/// </summary>
public sealed class ReadFileTool : ITool
{
    private const int MaxBytes = 8 * 1024; // 8 KB

    private readonly IReadOnlyList<string> _allowedRoots;

    /// <param name="allowedRoots">
    /// Normalised root paths. Any path not beginning with one of these (case-insensitive)
    /// is rejected. Pass an empty list to reject all paths.
    /// </param>
    public ReadFileTool(IReadOnlyList<string> allowedRoots)
    {
        _allowedRoots = allowedRoots
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList()
            .AsReadOnly();
    }

    public string Name           => "read_file";
    public string Description    => "Reads the UTF-8 contents of a file. Output is capped at 8 KB.";
    public string ArgumentSchema => "path";

    public Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (arguments.Count < 1 || string.IsNullOrWhiteSpace(arguments[0]))
            return Task.FromResult(ToolResult.Fail(Name, "Missing required argument: <path>."));

        var rawPath = arguments[0].Trim();

        if (!TryValidatePath(rawPath, out var fullPath, out var error))
            return Task.FromResult(ToolResult.Fail(Name, error!));

        return ReadCoreAsync(fullPath!, ct);
    }

    private async Task<ToolResult> ReadCoreAsync(string fullPath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(fullPath))
                return ToolResult.Fail(Name, $"File not found: {fullPath}");

            await using var fs = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);

            var buf = new byte[MaxBytes + 1];
            int read = await fs.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
            bool truncated = read > MaxBytes;

            var content = Encoding.UTF8.GetString(buf, 0, Math.Min(read, MaxBytes));
            if (truncated)
                content += "\n... [truncated]";

            return ToolResult.Ok(Name, content);
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
            return ToolResult.Fail(Name, $"Error reading file: {ex.Message}");
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
