using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Inference;

/// <summary>Per-tier configuration: model path, HTTP port, and whether to defer startup.</summary>
public sealed record BitNetTierConfig(string ModelPath, int Port, bool LazySpawn);

/// <summary>
/// Configuration for BitNet binary paths, model tiers, and server behaviour.
/// Bind via <c>IConfiguration.GetSection("BitNet").Bind(options)</c> or use <see cref="Default"/>.
/// </summary>
public sealed record BitNetOptions
{
    public string BinDirectory { get; init; } = @"C:/Software/research/BitNet/build/bin/Release";
    public string ServerExeName { get; init; } = "llama-server.exe";
    public string CliExeName { get; init; } = "llama-cli.exe";

    /// <summary>Per-tier model/port configuration. Defaults cover all four tiers.</summary>
    public Dictionary<ModelTier, BitNetTierConfig> Tiers { get; init; } = DefaultTiers();

    public int DefaultThreads { get; init; } = 4;
    public int DefaultContextSize { get; init; } = 2048;
    public string ServerHost { get; init; } = "127.0.0.1";

    /// <summary>How long to wait for llama-server.exe to become ready after launch.</summary>
    public TimeSpan ServerStartupTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Full path to <see cref="ServerExeName"/> inside <see cref="BinDirectory"/>.</summary>
    public string ServerExePath => Path.Combine(BinDirectory, ServerExeName);

    /// <summary>Full path to <see cref="CliExeName"/> inside <see cref="BinDirectory"/>.</summary>
    public string CliExePath => Path.Combine(BinDirectory, CliExeName);

    /// <summary>Returns a default-configured instance covering all four model tiers.</summary>
    public static BitNetOptions Default() => new() { Tiers = DefaultTiers() };

    private static Dictionary<ModelTier, BitNetTierConfig> DefaultTiers() => new()
    {
        [ModelTier.Reflex_1B] = new(
            @"C:/Software/research/BitNet/models/Falcon3-1B-Instruct-1.58bit/ggml-model-i2_s.gguf",
            Port: 8081,
            LazySpawn: false),

        [ModelTier.Standard_3B] = new(
            @"C:/Software/research/BitNet/models/Falcon3-3B-Instruct-1.58bit/ggml-model-i2_s.gguf",
            Port: 8082,
            LazySpawn: false),

        [ModelTier.Deliberate_7B] = new(
            @"C:/Software/research/BitNet/models/Falcon3-7B-Instruct-1.58bit/ggml-model-i2_s.gguf",
            Port: 8083,
            LazySpawn: true),

        [ModelTier.DeepReason_10B] = new(
            @"C:/Software/research/BitNet/models/Falcon3-10B-Instruct-1.58bit/ggml-model-i2_s.gguf",
            Port: 8084,
            LazySpawn: true),
    };
}
