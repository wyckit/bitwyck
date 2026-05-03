namespace Bitwyck.Core.Models;

/// <summary>
/// End-to-end output of one CognitiveLoop turn. Contains the final assistant message
/// plus the full chain-of-action (tool calls + observations) for replay/audit.
/// </summary>
public sealed record CognitiveCycleResult(
    string CorrelationId,
    SensoryEvent Trigger,
    RouteDecision Route,
    string FinalAnswer,
    IReadOnlyList<ToolCall> ToolCalls,
    IReadOnlyList<ToolResult> ToolResults,
    int TotalPromptTokens,
    int TotalCompletionTokens,
    TimeSpan Duration,
    bool DegradedMode = false,
    string? DegradedReason = null
);
