namespace Bitwyck.Core.Models;

/// <summary>
/// Per-turn cognitive state assembled by the harness before inference.
/// Carries everything needed to produce a final InferenceRequest.
/// </summary>
public sealed record CognitiveContext(
    SensoryEvent Trigger,
    UserIdentityState Identity,
    IReadOnlyList<Engram> RecalledEngrams,
    IReadOnlyList<InferenceMessage> ChatHistory,
    SystemBias Bias,
    int TokenBudget = 4096,
    string? CorrelationId = null
);
