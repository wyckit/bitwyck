namespace Bitwyck.Core.Models;

public enum MessageRole
{
    System = 0,
    User = 1,
    Assistant = 2,
    Tool = 3
}

public sealed record InferenceMessage(MessageRole Role, string Content);

public sealed record InferenceRequest(
    ModelTier Tier,
    IReadOnlyList<InferenceMessage> Messages,
    int MaxTokens = 512,
    double Temperature = 0.2,
    double TopP = 0.95,
    int? Seed = null,
    IReadOnlyList<string>? StopSequences = null
);

public sealed record InferenceResponse(
    string Content,
    int PromptTokens,
    int CompletionTokens,
    string Model,
    TimeSpan Duration,
    bool Truncated = false
);

public sealed record InferenceTokenChunk(string Token, bool IsFinal);
