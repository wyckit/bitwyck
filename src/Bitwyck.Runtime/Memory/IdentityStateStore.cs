using System.Text.Json;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Memory;

/// <summary>
/// JSON file-backed persistence store for <see cref="UserIdentityState"/>.
/// Atomic writes are achieved via write-to-temp-then-rename so a crash during
/// a save never leaves the file in a corrupt state.
/// </summary>
public sealed class IdentityStateStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;

    /// <summary>
    /// Creates a store that persists to <paramref name="filePath"/>.
    /// Defaults to <c>%LOCALAPPDATA%/bitwyck/identity.json</c> when null.
    /// </summary>
    public IdentityStateStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath();
    }

    /// <summary>
    /// Loads the identity state from disk.
    /// Returns <see cref="UserIdentityState.Empty()"/> if the file does not exist.
    /// </summary>
    public async Task<UserIdentityState> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return UserIdentityState.Empty();

        try
        {
            await using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            var state = await JsonSerializer
                .DeserializeAsync<UserIdentityState>(stream, _jsonOptions, ct)
                .ConfigureAwait(false);
            return state ?? UserIdentityState.Empty();
        }
        catch (JsonException)
        {
            // Corrupted file — return empty so the caller can rebuild.
            return UserIdentityState.Empty();
        }
    }

    /// <summary>
    /// Atomically saves <paramref name="state"/> to disk via temp-file + rename.
    /// </summary>
    public async Task SaveAsync(UserIdentityState state, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmpPath = _filePath + ".tmp";

        await using (var stream = new FileStream(
            tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true))
        {
            await JsonSerializer
                .SerializeAsync(stream, state, _jsonOptions, ct)
                .ConfigureAwait(false);
        }

        // Atomic rename — overwrites destination if it exists.
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    private static string DefaultFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "bitwyck", "identity.json");
    }
}
