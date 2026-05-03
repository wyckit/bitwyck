using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Lifecycle;
using Bitwyck.Runtime.Tooling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bitwyck.Tests.Lifecycle;

public class SkillSynthesizerTests
{
    private static SkillSynthesizer BuildSynthesizer(IToolRegistry? registry = null)
    {
        var compiler = new SkillCompiler(NullLogger<SkillCompiler>.Instance);
        registry ??= new ToolRegistry();
        return new SkillSynthesizer(compiler, registry, NullLogger<SkillSynthesizer>.Instance);
    }

    private static ToolCall MakeCall(string toolName, params string[] args) =>
        new(toolName, args, $"<call>{toolName}|{string.Join("|", args)}</call>");

    private static ToolChainTrace MakeTrace(string goal, params string[] toolNames)
    {
        var calls = toolNames.Select(n => MakeCall(n, "arg1")).ToList();
        var results = toolNames.Select(n => ToolResult.Ok(n, "ok")).ToList();
        return new ToolChainTrace(goal, calls.AsReadOnly(), results.AsReadOnly(), Succeeded: true);
    }

    // ── SynthesizeAsync returns a Proposed skill ──────────────────────────────

    [Fact]
    public async Task SynthesizeAsync_ThreeStepTrace_ReturnsSynthesizedSkill()
    {
        var synth = BuildSynthesizer();
        var trace = MakeTrace("do something useful", "read_file", "transform", "write_file");

        var skill = await synth.SynthesizeAsync(trace);

        Assert.Equal(SkillLifecycleState.Proposed, skill.State);
        Assert.NotEmpty(skill.SourceCode);
        Assert.StartsWith("synth_", skill.ToolName);
    }

    // ── SourceCode contains each step's tool name ─────────────────────────────

    [Fact]
    public async Task SynthesizeAsync_SourceCodeContainsAllStepToolNames()
    {
        var synth = BuildSynthesizer();
        var trace = MakeTrace("multi-step goal", "alpha_tool", "beta_tool", "gamma_tool");

        var skill = await synth.SynthesizeAsync(trace);

        Assert.Contains("alpha_tool", skill.SourceCode);
        Assert.Contains("beta_tool", skill.SourceCode);
        Assert.Contains("gamma_tool", skill.SourceCode);
    }

    // ── ToolName starts with "synth_" ─────────────────────────────────────────

    [Fact]
    public async Task SynthesizeAsync_ToolNameStartsWithSynth()
    {
        var synth = BuildSynthesizer();
        var trace = MakeTrace("test goal", "echo_tool");

        var skill = await synth.SynthesizeAsync(trace);

        Assert.StartsWith("synth_", skill.ToolName);
    }

    // ── CompileAndLoadAsync succeeds with a real registry and stub tools ──────

    [Fact]
    public async Task CompileAndLoadAsync_SimpleTrace_SucceedsAndRegisters()
    {
        var registry = new ToolRegistry();

        // Register a minimal in-memory stub so the synthesized code can TryGet it
        registry.Register(new EchoTool("echo_tool"));

        var synth = BuildSynthesizer(registry);
        var trace = MakeTrace("echo a thing", "echo_tool");

        var skill = await synth.SynthesizeAsync(trace);
        var tool = await synth.CompileAndLoadAsync(skill);

        Assert.NotNull(tool);
        Assert.StartsWith("synth_", tool!.Name);

        // Verify the compiled skill is now in the registry
        Assert.True(registry.TryGet(tool.Name, out _));
    }

    // ── CompileAndLoadAsync: compiled skill is executable ─────────────────────

    [Fact]
    public async Task CompileAndLoadAsync_CompiledSkillExecutes_ReturnsOk()
    {
        var registry = new ToolRegistry();
        registry.Register(new EchoTool("echo_tool"));

        var synth = BuildSynthesizer(registry);
        var trace = MakeTrace("execute compiled skill", "echo_tool");

        var skill = await synth.SynthesizeAsync(trace);
        var tool = await synth.CompileAndLoadAsync(skill);

        Assert.NotNull(tool);
        var result = await tool!.ExecuteAsync(new[] { "arg1" });
        Assert.True(result.Success, $"Skill execution failed: {result.Error}");
    }

    // ── Helper: minimal ITool stub ────────────────────────────────────────────

    private sealed class EchoTool : ITool
    {
        public EchoTool(string name) => Name = name;

        public string Name { get; }
        public string Description => "Echoes arguments.";
        public string ArgumentSchema => "arg";

        public Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
            => Task.FromResult(ToolResult.Ok(Name, string.Join(", ", arguments)));
    }
}
