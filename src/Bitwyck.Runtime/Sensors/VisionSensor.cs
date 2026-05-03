using System.Diagnostics;
using System.Text;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Sensors;

/// <summary>
/// Vision sensor that shells out to <c>llama-llava-cli.exe</c> to describe an image.
/// </summary>
/// <remarks>
/// Required binaries/models at construction time:
/// <list type="bullet">
///   <item><paramref name="llavaCliPath"/> — path to <c>llama-llava-cli.exe</c></item>
///   <item><paramref name="llavaModelPath"/> — LLaVA GGUF model file</item>
///   <item><paramref name="mmprojPath"/> — multimodal projector file required by LLaVA</item>
/// </list>
/// <see cref="IsAvailableAsync"/> returns <c>false</c> when any of the three files is missing,
/// and <see cref="CaptureAsync"/> throws <see cref="SensorUnavailableException"/> in that case.
/// </remarks>
public sealed class VisionSensor : ISensor
{
    private const string DefaultPrompt = "Describe this image briefly.";
    private const int DefaultMaxTokens = 200;
    private const int DefaultThreads = 4;

    private readonly string _llavaCliPath;
    private readonly string _llavaModelPath;
    private readonly string _mmprojPath;

    public VisionSensor(string llavaCliPath, string llavaModelPath, string mmprojPath)
    {
        _llavaCliPath = llavaCliPath ?? throw new ArgumentNullException(nameof(llavaCliPath));
        _llavaModelPath = llavaModelPath ?? throw new ArgumentNullException(nameof(llavaModelPath));
        _mmprojPath = mmprojPath ?? throw new ArgumentNullException(nameof(mmprojPath));
    }

    public SensoryChannel Channel => SensoryChannel.Vision;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var available =
            File.Exists(_llavaCliPath) &&
            File.Exists(_llavaModelPath) &&
            File.Exists(_mmprojPath);

        return Task.FromResult(available);
    }

    public async Task<SensoryEvent> CaptureAsync(object input, CancellationToken ct = default)
    {
        var available = await IsAvailableAsync(ct).ConfigureAwait(false);
        if (!available)
        {
            var missing = new List<string>();
            if (!File.Exists(_llavaCliPath)) missing.Add($"cli='{_llavaCliPath}'");
            if (!File.Exists(_llavaModelPath)) missing.Add($"model='{_llavaModelPath}'");
            if (!File.Exists(_mmprojPath)) missing.Add($"mmproj='{_mmprojPath}'");

            throw new SensorUnavailableException(
                SensoryChannel.Vision,
                $"VisionSensor: missing file(s): {string.Join(", ", missing)}.");
        }

        var imagePath = input as string
            ?? throw new ArgumentException(
                "VisionSensor.CaptureAsync expects a string image path.", nameof(input));

        var description = await RunLlavaAsync(imagePath, ct).ConfigureAwait(false);

        return new SensoryEvent(
            Guid.NewGuid().ToString("N"),
            SensoryChannel.Vision,
            description,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["imagePath"] = imagePath,
            });
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<string> RunLlavaAsync(string imagePath, CancellationToken ct)
    {
        // Escape double-quotes inside argument values.
        static string Esc(string s) => s.Replace("\"", "\\\"");

        var args = string.Join(" ",
            $"-m \"{Esc(_llavaModelPath)}\"",
            $"--mmproj \"{Esc(_mmprojPath)}\"",
            $"--image \"{Esc(imagePath)}\"",
            $"-p \"{Esc(DefaultPrompt)}\"",
            $"-n {DefaultMaxTokens}",
            $"-t {DefaultThreads}");

        var psi = new ProcessStartInfo(_llavaCliPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var raw = stdoutBuilder.ToString().Trim();

        // llama-llava-cli echoes the prompt; strip it if present.
        if (raw.StartsWith(DefaultPrompt, StringComparison.OrdinalIgnoreCase))
            raw = raw[DefaultPrompt.Length..].Trim();

        return raw;
    }
}
