using Bitwyck.Core.Models;
using Bitwyck.Runtime.Memory;

namespace Bitwyck.Tests.Memory;

public sealed class IdentityStateStoreTests : IDisposable
{
    private readonly string _tempFile;

    public IdentityStateStoreTests()
    {
        // Generate a unique temp path that does NOT exist yet.
        _tempFile = Path.GetTempFileName();
        File.Delete(_tempFile); // Remove it so the store treats it as missing on first Load.
    }

    public void Dispose()
    {
        // Clean up both the main file and any .tmp leftover.
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        if (File.Exists(_tempFile + ".tmp")) File.Delete(_tempFile + ".tmp");
    }

    // ── Load on missing file → UserIdentityState.Empty() ─────────────────────

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmpty()
    {
        var store = new IdentityStateStore(_tempFile);
        var state = await store.LoadAsync();

        Assert.Equal(UserIdentityState.Empty().Persona, state.Persona);
        Assert.Equal(0, state.Version);
        Assert.Empty(state.Preferences);
        Assert.Empty(state.EvolvingGoals);
    }

    // ── Save then Load round-trips all fields ────────────────────────────────

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAllFields()
    {
        var original = new UserIdentityState(
            Persona: "Test persona for round-trip",
            Preferences: new[] { "dark mode", "compact layout" },
            EvolvingGoals: new[] { "learn Rust", "ship v2" },
            Constraints: new[] { "no swearing" },
            KnownFacts: new Dictionary<string, string> { ["lang"] = "C#", ["os"] = "Windows" },
            LastUpdated: new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
            Version: 7
        );

        var store = new IdentityStateStore(_tempFile);
        await store.SaveAsync(original);

        var loaded = await store.LoadAsync();

        Assert.Equal(original.Persona, loaded.Persona);
        Assert.Equal(original.Version, loaded.Version);
        Assert.Equal(original.Preferences, loaded.Preferences);
        Assert.Equal(original.EvolvingGoals, loaded.EvolvingGoals);
        Assert.Equal(original.Constraints, loaded.Constraints);
        Assert.Equal(original.KnownFacts["lang"], loaded.KnownFacts["lang"]);
        Assert.Equal(original.KnownFacts["os"], loaded.KnownFacts["os"]);
    }

    // ── Atomic write: file exists and parses after save ──────────────────────

    [Fact]
    public async Task SaveAsync_ProducesValidJsonFile()
    {
        var state = UserIdentityState.Empty() with { Persona = "atomically written", Version = 3 };
        var store = new IdentityStateStore(_tempFile);

        await store.SaveAsync(state);

        Assert.True(File.Exists(_tempFile), "The file should exist after SaveAsync.");
        // Verify the temp file is cleaned up (atomic rename happened).
        Assert.False(File.Exists(_tempFile + ".tmp"), "The .tmp staging file should be gone.");

        // Verify the file parses correctly via a fresh store instance.
        var store2 = new IdentityStateStore(_tempFile);
        var loaded = await store2.LoadAsync();
        Assert.Equal("atomically written", loaded.Persona);
        Assert.Equal(3, loaded.Version);
    }

    // ── Overwrite: second save replaces first ────────────────────────────────

    [Fact]
    public async Task SaveAsync_CalledTwice_SecondWriteWins()
    {
        var store = new IdentityStateStore(_tempFile);

        var first = UserIdentityState.Empty() with { Persona = "first", Version = 1 };
        var second = UserIdentityState.Empty() with { Persona = "second", Version = 2 };

        await store.SaveAsync(first);
        await store.SaveAsync(second);

        var loaded = await store.LoadAsync();
        Assert.Equal("second", loaded.Persona);
        Assert.Equal(2, loaded.Version);
    }

    // ── Corrupted file → returns Empty ──────────────────────────────────────

    [Fact]
    public async Task LoadAsync_CorruptedFile_ReturnsEmpty()
    {
        File.WriteAllText(_tempFile, "{ this is not valid json {{{{");
        var store = new IdentityStateStore(_tempFile);

        var state = await store.LoadAsync();

        Assert.Equal(UserIdentityState.Empty().Persona, state.Persona);
        Assert.Equal(0, state.Version);
    }
}
