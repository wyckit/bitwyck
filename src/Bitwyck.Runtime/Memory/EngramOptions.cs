using System.Runtime.InteropServices;

namespace Bitwyck.Runtime.Memory;

/// <summary>
/// Configuration for the Engram memory subsystem.
/// </summary>
public sealed record EngramOptions
{
    /// <summary>
    /// Path to the SQLite database file. Defaults to
    /// <c>%LOCALAPPDATA%/bitwyck/engram.db</c> on Windows or the platform equivalent.
    /// </summary>
    public string DatabasePath { get; init; } = DefaultDatabasePath();

    /// <summary>
    /// Optional path to the ONNX bge-micro-v2 model file. When null a deterministic
    /// hash-based fallback embedder is used so the build stays functional without the model.
    /// </summary>
    public string? EmbeddingModelPath { get; init; }

    /// <summary>
    /// Namespace used for storing and recalling episodic entries.
    /// </summary>
    public string DefaultNamespace { get; init; } = "bitwyck-episodic";

    private static string DefaultDatabasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "bitwyck", "engram.db");
    }
}
