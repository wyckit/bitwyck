using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Cognition;
using Bitwyck.Runtime.Inference;
using Bitwyck.Runtime.Tooling;
using Bitwyck.Runtime.Tooling.BuiltIns;
using Bitwyck.Tests.Fakes;

namespace Bitwyck.Tests.Integration;

/// <summary>
/// End-to-end integration tests that wire the real CognitiveLoop with real subsystems
/// (XmlToolInterceptor, ToolRegistry, EnergyManager, PromptCompiler, DefaultSystemBiasProvider)
/// backed by FakeBitNetClient and in-memory stores.
///
/// Each [Fact] exercises one complete sense -> recall -> route -> execute -> intercept -> commit cycle.
/// </summary>
public sealed class EndToEnd_FakeLlmTests
{
    // ── Helper: build a fully wired CognitiveLoop ─────────────────────────────

    private static CognitiveLoop BuildLoop(
        FakeBitNetClient client,
        InMemoryEngramStore store,
        InMemoryIdentityStore identityStore,
        ToolRegistry registry)
    {
        var promptCompiler = new PromptCompiler(registry);

        return new CognitiveLoop(
            inference: client,
            router: new EnergyManager(),
            bias: new DefaultSystemBiasProvider(),
            tools: registry,
            promptCompiler: promptCompiler,
            recall: async (ev, ident, ct) =>
                await store.SearchAsync(new EngramQuery(ev.Payload, "bitwyck-episodic"), ct),
            intercept: async (stream, handler, ct) =>
            {
                var interceptor = new XmlToolInterceptor();
                var result = await interceptor.ProcessAsync(
                    stream,
                    new XmlToolInterceptor.ToolHandler((call, ictCt) => handler(call, ictCt)),
                    ct);
                return new InterceptionOutcome(result.AssembledOutput, result.ToolCalls, result.ToolResults);
            },
            commit: async (cycleResult, ct) =>
            {
                await store.StoreAsync(new Engram(
                    Id: $"episodic-{cycleResult.CorrelationId}",
                    Namespace: "bitwyck-episodic",
                    Text: cycleResult.FinalAnswer,
                    Category: "turn"), ct);
            },
            identityStore: identityStore);
    }

    // ── Scenario A: pure conversation, no tool call ───────────────────────────

    [Fact]
    public async Task ScenarioA_PureConversation_NoToolCall()
    {
        // Arrange
        var client = new FakeBitNetClient { DefaultResponse = "Hello from bitwyck" };
        var store = new InMemoryEngramStore();
        var identityStore = new InMemoryIdentityStore();
        var registry = new ToolRegistry();

        var loop = BuildLoop(client, store, identityStore, registry);

        // Act
        var result = await loop.RunAsync(SensoryEvent.FromText("hi"));

        // Assert
        Assert.Equal("Hello from bitwyck", result.FinalAnswer);
        Assert.Empty(result.ToolCalls);
        Assert.False(result.DegradedMode);
        Assert.Single(client.CapturedRequests);
    }

    // ── Scenario B: single tool call reads a file ─────────────────────────────

    [Fact]
    public async Task ScenarioB_SingleToolCall_ReadFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            // Arrange — seed the file
            var filePath = Path.Combine(tempRoot, "test.txt");
            await File.WriteAllTextAsync(filePath, "secret-payload-42");

            var client = new FakeBitNetClient
            {
                DefaultResponse = $"I'll read the file. <call>read_file|{filePath}</call> Done."
            };
            var store = new InMemoryEngramStore();
            var identityStore = new InMemoryIdentityStore();
            var registry = new ToolRegistry();
            registry.Register(new ReadFileTool(new[] { tempRoot }));

            var loop = BuildLoop(client, store, identityStore, registry);

            // Act
            var result = await loop.RunAsync(SensoryEvent.FromText("read the file"));

            // Assert
            Assert.Single(result.ToolCalls);
            Assert.Equal("read_file", result.ToolCalls[0].ToolName);
            Assert.Single(result.ToolResults);
            Assert.True(result.ToolResults[0].Success);
            Assert.Contains("<observation>", result.FinalAnswer);
            Assert.Contains("secret-payload-42", result.FinalAnswer);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ── Scenario C: unknown tool call recovers gracefully ─────────────────────

    [Fact]
    public async Task ScenarioC_UnknownToolCall_FailsGracefully()
    {
        // Arrange
        var client = new FakeBitNetClient
        {
            DefaultResponse = "<call>nonexistent_tool|whatever</call>"
        };
        var store = new InMemoryEngramStore();
        var identityStore = new InMemoryIdentityStore();
        var registry = new ToolRegistry(); // empty — nonexistent_tool not registered

        var loop = BuildLoop(client, store, identityStore, registry);

        // Act
        var result = await loop.RunAsync(SensoryEvent.FromText("do something"));

        // Assert
        Assert.Single(result.ToolResults);
        Assert.False(result.ToolResults[0].Success);
        Assert.Contains("<observation error=\"true\">", result.FinalAnswer);
    }

    // ── Scenario D: multi-tool chain: write then read ─────────────────────────

    [Fact]
    public async Task ScenarioD_MultiToolChain_WriteThenRead()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var filePath = Path.Combine(tempRoot, "foo.txt");

            // Arrange — single LLM response emits two tool calls
            var llmResponse = $"<call>write_file|{filePath}|hello world</call> <call>read_file|{filePath}</call>";
            var client = new FakeBitNetClient { DefaultResponse = llmResponse };
            var store = new InMemoryEngramStore();
            var identityStore = new InMemoryIdentityStore();
            var registry = new ToolRegistry();
            registry.Register(new WriteFileTool(new[] { tempRoot }));
            registry.Register(new ReadFileTool(new[] { tempRoot }));

            var loop = BuildLoop(client, store, identityStore, registry);

            // Act
            var result = await loop.RunAsync(SensoryEvent.FromText("write and read"));

            // Assert
            Assert.Equal(2, result.ToolCalls.Count);
            Assert.True(result.ToolResults[0].Success, "write_file should succeed");
            Assert.True(result.ToolResults[1].Success, "read_file should succeed");
            Assert.Contains("hello world", result.FinalAnswer);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ── Scenario E: engram commit fires and persists the answer ───────────────

    [Fact]
    public async Task ScenarioE_EngramCommit_StoresAnswerAfterTurn()
    {
        // Arrange
        var client = new FakeBitNetClient { DefaultResponse = "committed answer" };
        var store = new InMemoryEngramStore();
        var identityStore = new InMemoryIdentityStore();
        var registry = new ToolRegistry();

        var loop = BuildLoop(client, store, identityStore, registry);

        // Assert: store is empty before the turn
        Assert.Equal(0, await store.CountAsync());

        // Act
        await loop.RunAsync(SensoryEvent.FromText("remember me"));

        // Assert: commit lambda fired and stored exactly one engram
        var count = await store.CountAsync();
        Assert.True(count > 0, "At least one engram should be stored after a turn.");
    }
}
