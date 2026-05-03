using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Tooling;

namespace Bitwyck.Tests.Tooling;

public class ToolRegistryTests
{
    private static ITool MakeTool(string name, string description = "a tool") =>
        new StubTool(name, description);

    // ── Register + TryGet ─────────────────────────────────────────────────────

    [Fact]
    public void Register_ThenTryGet_ReturnsTool()
    {
        var registry = new ToolRegistry();
        var tool = MakeTool("my_tool");

        registry.Register(tool);
        var found = registry.TryGet("my_tool", out var retrieved);

        Assert.True(found);
        Assert.Same(tool, retrieved);
    }

    // ── TryGet unknown name ───────────────────────────────────────────────────

    [Fact]
    public void TryGet_UnknownName_ReturnsFalseAndNull()
    {
        var registry = new ToolRegistry();

        var found = registry.TryGet("nonexistent", out var retrieved);

        Assert.False(found);
        Assert.Null(retrieved);
    }

    // ── All() ─────────────────────────────────────────────────────────────────

    [Fact]
    public void All_ReturnsEveryRegisteredTool()
    {
        var registry = new ToolRegistry();
        var tool1 = MakeTool("alpha");
        var tool2 = MakeTool("beta");
        var tool3 = MakeTool("gamma");

        registry.Register(tool1);
        registry.Register(tool2);
        registry.Register(tool3);

        var all = registry.All();

        Assert.Equal(3, all.Count);
        Assert.Contains(all, t => t.Name == "alpha");
        Assert.Contains(all, t => t.Name == "beta");
        Assert.Contains(all, t => t.Name == "gamma");
    }

    // ── ToPromptManifest ──────────────────────────────────────────────────────

    [Fact]
    public void ToPromptManifest_ContainsEachToolNameAndDescription()
    {
        var registry = new ToolRegistry();
        registry.Register(MakeTool("read_file", "Reads a file from disk."));
        registry.Register(MakeTool("write_file", "Writes content to a file."));

        var manifest = registry.ToPromptManifest();

        Assert.Contains("read_file", manifest);
        Assert.Contains("write_file", manifest);
        Assert.Contains("Reads a file from disk.", manifest);
        Assert.Contains("Writes content to a file.", manifest);
    }

    [Fact]
    public void ToPromptManifest_WhenEmpty_ReturnsSentinel()
    {
        var registry = new ToolRegistry();

        var manifest = registry.ToPromptManifest();

        Assert.Equal("(no tools)", manifest);
    }

    // ── Re-registering same name replaces ────────────────────────────────────

    [Fact]
    public void ReRegisterSameName_ReplacesPrevious()
    {
        var registry = new ToolRegistry();
        var first  = MakeTool("my_tool", "first description");
        var second = MakeTool("my_tool", "second description");

        registry.Register(first);
        registry.Register(second);

        registry.TryGet("my_tool", out var retrieved);

        // The second registration should win
        Assert.Same(second, retrieved);
        // Only one entry in All()
        Assert.Single(registry.All());
    }

    // ── Helper stub ──────────────────────────────────────────────────────────

    private sealed class StubTool : ITool
    {
        public StubTool(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }
        public string Description { get; }
        public string ArgumentSchema => string.Empty;

        public Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
            => Task.FromResult(ToolResult.Ok(Name, "stub"));
    }
}
