using System.Collections.Concurrent;
using Bitwyck.Core.Interfaces;

namespace Bitwyck.Runtime.Lifecycle;

/// <summary>
/// In-memory state machine for tracking <see cref="SynthesizedSkill"/> instances
/// through their lifecycle. Thread-safe. Persistence is out-of-scope for this
/// phase — a JSON persistence layer may be layered on in Phase D.
/// </summary>
public sealed class SkillLifecycle
{
    // Internal envelope that pairs the skill with mutable state & notes.
    private sealed record Entry(SynthesizedSkill Skill, string? RejectionReason = null);

    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);

    // -------------------------------------------------------------------------
    // Write operations
    // -------------------------------------------------------------------------

    /// <summary>Registers a newly synthesized skill in the <see cref="SkillLifecycleState.Proposed"/> state.</summary>
    /// <exception cref="ArgumentException">Thrown if the skill is not in the Proposed state.</exception>
    public void SubmitProposed(SynthesizedSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        if (skill.State != SkillLifecycleState.Proposed)
            throw new ArgumentException(
                $"Only Proposed skills may be submitted; got state '{skill.State}'.",
                nameof(skill));

        _store[skill.SkillId] = new Entry(skill);
    }

    /// <summary>Advances a skill from <see cref="SkillLifecycleState.Proposed"/> to <see cref="SkillLifecycleState.Sandboxed"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the skill is not found or is not in the Proposed state.</exception>
    public SynthesizedSkill MoveToSandboxed(string skillId)
    {
        var updated = Transition(skillId, SkillLifecycleState.Proposed, SkillLifecycleState.Sandboxed);
        return updated;
    }

    /// <summary>Advances a skill from <see cref="SkillLifecycleState.Sandboxed"/> to <see cref="SkillLifecycleState.UnderReview"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the skill is not found or is not in the Sandboxed state.</exception>
    public SynthesizedSkill MoveToUnderReview(string skillId)
    {
        return Transition(skillId, SkillLifecycleState.Sandboxed, SkillLifecycleState.UnderReview);
    }

    /// <summary>Promotes a skill from <see cref="SkillLifecycleState.UnderReview"/> to <see cref="SkillLifecycleState.Promoted"/>.</summary>
    /// <returns>The promoted <see cref="SynthesizedSkill"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the skill is not found or is not in the UnderReview state.</exception>
    public SynthesizedSkill Promote(string skillId)
    {
        return Transition(skillId, SkillLifecycleState.UnderReview, SkillLifecycleState.Promoted);
    }

    /// <summary>
    /// Rejects a skill from any non-terminal state, recording an optional reason.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the skill is not found, or is already in the
    /// <see cref="SkillLifecycleState.Promoted"/> or <see cref="SkillLifecycleState.Rejected"/> state.
    /// </exception>
    public SynthesizedSkill Reject(string skillId, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        if (!_store.TryGetValue(skillId, out var current))
            throw new InvalidOperationException($"Skill '{skillId}' not found.");

        if (current.Skill.State is SkillLifecycleState.Rejected)
            throw new InvalidOperationException($"Skill '{skillId}' is already Rejected.");

        if (current.Skill.State is SkillLifecycleState.Promoted)
            throw new InvalidOperationException(
                $"Cannot reject skill '{skillId}' because it is already Promoted.");

        var updated = current.Skill with
        {
            State = SkillLifecycleState.Rejected,
            ReviewNotes = reason
        };

        _store[skillId] = new Entry(updated, reason);
        return updated;
    }

    // -------------------------------------------------------------------------
    // Read operations
    // -------------------------------------------------------------------------

    /// <summary>Returns the current skill record, or <c>null</c> if not found.</summary>
    public SynthesizedSkill? Get(string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        return _store.TryGetValue(skillId, out var e) ? e.Skill : null;
    }

    /// <summary>Returns all tracked skills (snapshot, unordered).</summary>
    public IReadOnlyList<SynthesizedSkill> All() =>
        _store.Values.Select(e => e.Skill).ToList();

    /// <summary>Returns all skills currently in the given <paramref name="state"/>.</summary>
    public IReadOnlyList<SynthesizedSkill> ByState(SkillLifecycleState state) =>
        _store.Values
              .Where(e => e.Skill.State == state)
              .Select(e => e.Skill)
              .ToList();

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private SynthesizedSkill Transition(
        string skillId,
        SkillLifecycleState expectedCurrent,
        SkillLifecycleState next)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        if (!_store.TryGetValue(skillId, out var current))
            throw new InvalidOperationException($"Skill '{skillId}' not found.");

        if (current.Skill.State != expectedCurrent)
            throw new InvalidOperationException(
                $"Cannot move skill '{skillId}' to {next}: expected state {expectedCurrent} but found {current.Skill.State}.");

        var updated = current.Skill with { State = next };
        _store[skillId] = new Entry(updated);
        return updated;
    }
}
