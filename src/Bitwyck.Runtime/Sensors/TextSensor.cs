using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Sensors;

/// <summary>
/// Synchronous pass-through sensor for plain text input.
/// Accepts a <see cref="string"/> (or an object with a <c>Text</c> property) and wraps
/// it in a <see cref="SensoryEvent"/> on <see cref="SensoryChannel.Text"/>.
/// </summary>
public sealed class TextSensor : ISensor
{
    public SensoryChannel Channel => SensoryChannel.Text;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<SensoryEvent> CaptureAsync(object input, CancellationToken ct = default)
    {
        var text = input switch
        {
            string s => s,
            // Support anonymous objects like new { Text = "..." } via reflection.
            _ when TryExtractText(input, out var extracted) => extracted!,
            _ => throw new ArgumentException(
                $"TextSensor expects a string or an object with a Text property, got {input?.GetType().Name ?? "null"}.",
                nameof(input))
        };

        var ev = SensoryEvent.FromText(text, source: "text");
        return Task.FromResult(ev);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static bool TryExtractText(object input, out string? text)
    {
        text = null;
        if (input is null) return false;

        var prop = input.GetType().GetProperty("Text");
        if (prop is null || prop.PropertyType != typeof(string)) return false;

        text = (string?)prop.GetValue(input);
        return text is not null;
    }
}
