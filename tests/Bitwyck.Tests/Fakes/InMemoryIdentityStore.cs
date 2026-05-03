using Bitwyck.Core.Models;
using Bitwyck.Runtime.Lifecycle;

namespace Bitwyck.Tests.Fakes;

public sealed class InMemoryIdentityStore : IIdentityStore
{
    private UserIdentityState _state = UserIdentityState.Empty();
    private readonly object _lock = new();

    public Task<UserIdentityState> LoadAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_state);
    }

    public Task SaveAsync(UserIdentityState state, CancellationToken ct = default)
    {
        lock (_lock) _state = state;
        return Task.CompletedTask;
    }
}
