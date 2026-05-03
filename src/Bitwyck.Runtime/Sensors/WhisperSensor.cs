using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Sensors;

/// <summary>
/// Audio transcription sensor backed by Whisper.NET (future integration).
/// When <paramref name="whisperModelPath"/> points to an existing model file the sensor
/// reports as available; otherwise <see cref="CaptureAsync"/> throws
/// <see cref="SensorUnavailableException"/> and the cognitive loop degrades gracefully.
/// </summary>
/// <remarks>
/// Real Whisper.NET integration is deferred. The <see cref="CaptureAsync"/> implementation
/// currently returns a stub payload so the harness pipeline can be exercised end-to-end
/// without a downloaded model.
/// </remarks>
public sealed class WhisperSensor : ISensor
{
    private readonly string? _whisperModelPath;

    public WhisperSensor(string? whisperModelPath = null)
    {
        _whisperModelPath = whisperModelPath;
    }

    public SensoryChannel Channel => SensoryChannel.Audio;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(_whisperModelPath is not null && File.Exists(_whisperModelPath));

    public async Task<SensoryEvent> CaptureAsync(object input, CancellationToken ct = default)
    {
        var available = await IsAvailableAsync(ct).ConfigureAwait(false);
        if (!available)
            throw new SensorUnavailableException(
                SensoryChannel.Audio,
                _whisperModelPath is null
                    ? "WhisperSensor: no model path configured."
                    : $"WhisperSensor: model file not found at '{_whisperModelPath}'.");

        // Stub: real implementation would call Whisper.NET to transcribe input.
        var inputRepr = input?.ToString() ?? string.Empty;
        var stubPayload = $"[transcription stub for input: {inputRepr}]";

        return new SensoryEvent(
            Guid.NewGuid().ToString("N"),
            SensoryChannel.Audio,
            stubPayload,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["stub"] = "true",
                ["modelPath"] = _whisperModelPath!,
            });
    }
}
