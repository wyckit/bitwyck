using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Lifecycle;

/// <summary>
/// Persistence abstraction for <see cref="UserIdentityState"/>.
/// The Memory subsystem's <c>IdentityStateStore</c> implements this.
/// Phase D DI wiring registers the concrete type against this interface.
/// </summary>
public interface IIdentityStore
{
    Task<UserIdentityState> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(UserIdentityState state, CancellationToken ct = default);
}
