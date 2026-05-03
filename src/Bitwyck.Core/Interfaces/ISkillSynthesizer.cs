using Bitwyck.Core.Models;

namespace Bitwyck.Core.Interfaces;

public sealed record ToolChainTrace(
    string Goal,
    IReadOnlyList<ToolCall> Calls,
    IReadOnlyList<ToolResult> Results,
    bool Succeeded
);

public enum SkillLifecycleState
{
    Proposed = 0,
    Sandboxed = 1,
    UnderReview = 2,
    Promoted = 3,
    Rejected = 4
}

public sealed record SynthesizedSkill(
    string SkillId,
    string SourceCode,
    string ToolName,
    SkillLifecycleState State,
    DateTimeOffset CreatedAt,
    string? ReviewNotes = null
);

public interface ISkillSynthesizer
{
    /// <summary>Translate a successful multi-step tool chain into a new ITool C# source file.</summary>
    Task<SynthesizedSkill> SynthesizeAsync(ToolChainTrace trace, CancellationToken ct = default);

    /// <summary>Compile a synthesized skill to a loadable Type and register it.</summary>
    Task<ITool?> CompileAndLoadAsync(SynthesizedSkill skill, CancellationToken ct = default);
}
