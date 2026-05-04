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
    public int DefaultContextSize { get; init; } = 8192;
    public string ServerHost { get; init; } = "127.0.0.1";

    /// <summary>
    /// Hard upper bound on prompt character length before <see cref="BitNetCliClient"/>
    /// refuses to invoke llama-cli. With the default 1 MB Windows thread stack the
    /// 1.58-bit kernels stack-overflow above ~870 chars. After bumping llama-cli's
    /// stack reserve to 8 MB (via <c>editbin /STACK:8388608</c>) the practical
    /// ceiling is ~30 000 chars (limited by the model's 8192-token context, not
    /// by stack pressure). 24000 fits inside the model's natural 8192-token context.
    /// Set to 0 to disable the guard.
    /// </summary>
    public int MaxPromptChars { get; init; } = 24000;

    /// <summary>
    /// Tier used for "deep" work — the reduce step of <c>SummarizeTool</c>,
    /// the optional escalation path in <c>MapReduceTool</c>. Defaults to
    /// Standard_3B (Falcon3-3B). The 7B/10B BitNet kernels in this build
    /// have additional bugs that crash on prompts above ~600 chars, so they
    /// aren't usable for chunked work; 3B's envelope is ~2000 chars.
    /// </summary>
    public ModelTier DeepTier { get; init; } = ModelTier.Standard_3B;

    /// <summary>
    /// Per-call prompt cap when invoking the deep tier. Smaller than
    /// <see cref="MaxPromptChars"/> because the bigger models have tighter
    /// stable envelopes. SummarizeTool falls back to the fast tier when a
    /// would-be deep-tier prompt exceeds this cap.
    /// </summary>
    public int DeepTierMaxPromptChars { get; init; } = 1800;

    /// <summary>
    /// RoPE scaling type passed to llama-cli ("none", "linear", "yarn"). Use "linear"
    /// in combination with <see cref="RopeFreqScale"/> &lt; 1 to extend the model
    /// past its trained context window — at the cost of some output quality.
    /// </summary>
    public string RopeScalingType { get; init; } = "none";

    /// <summary>
    /// RoPE frequency scale. 1.0 = native context (default). 0.5 = 2× extended
    /// (e.g. 8192 → 16384 with linear scaling). Lower = more extension, more
    /// quality loss.
    /// </summary>
    public double RopeFreqScale { get; init; } = 1.0;

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
