using Bitwyck.Core.Models;

namespace Bitwyck.Core.Interfaces;

public interface ICognitiveRouter
{
    /// <summary>
    /// Decide which model tier should handle this turn given the recall results,
    /// the trigger, and the user identity.
    /// </summary>
    RouteDecision Route(SensoryEvent trigger, IReadOnlyList<Engram> recalled, UserIdentityState identity);
}
