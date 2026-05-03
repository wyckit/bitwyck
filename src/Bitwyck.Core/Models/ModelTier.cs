namespace Bitwyck.Core.Models;

/// <summary>
/// Cognitive routing tier. Maps to a BitNet model size and execution profile.
/// System 1 (Reflex) is fast and low-energy; System 2 (DeepReason) is slow and deliberate.
/// </summary>
public enum ModelTier
{
    /// <summary>1B param model — high-confidence memory matches, tool argument formatting, simple summarization.</summary>
    Reflex_1B = 0,

    /// <summary>3B param model — default conversational tier, light reasoning.</summary>
    Standard_3B = 1,

    /// <summary>7B param model — multi-step planning, code generation, structured output.</summary>
    Deliberate_7B = 2,

    /// <summary>10B param model — novel problems, deep reasoning, dialectic synthesis.</summary>
    DeepReason_10B = 3
}
