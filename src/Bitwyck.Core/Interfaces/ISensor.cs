using Bitwyck.Core.Models;

namespace Bitwyck.Core.Interfaces;

public interface ISensor
{
    SensoryChannel Channel { get; }

    /// <summary>True if the sensor's underlying resource (model, hardware, server) is reachable.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Convert a raw input (string/file path/bytes) into a normalized SensoryEvent.</summary>
    Task<SensoryEvent> CaptureAsync(object input, CancellationToken ct = default);
}

/// <summary>Thrown by sensors whose backing model/hardware is missing. Caught by CognitiveLoop and degraded.</summary>
public sealed class SensorUnavailableException : Exception
{
    public SensoryChannel Channel { get; }
    public SensorUnavailableException(SensoryChannel channel, string message) : base(message)
    {
        Channel = channel;
    }
}
