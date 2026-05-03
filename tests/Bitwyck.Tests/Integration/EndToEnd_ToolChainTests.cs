using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Cognition;
using Bitwyck.Runtime.Inference;
using Bitwyck.Runtime.Tooling;
using Bitwyck.Runtime.Tooling.BuiltIns;
using Bitwyck.Tests.Fakes;

namespace Bitwyck.Tests.Integration;

/// <summary>
/// Secondary tool-chain coverage: multi-file listing + reading in a single turn,
/// and graceful degraded-mode handling when all model tiers are unavailable.
/// </summary>
public sealed class EndToEnd_ToolChainTests
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

    // ── Test F: list then read in a single turn ───────────────────────────────

    [Fact]
    public async Task TestF_ListThenRead_BothCallsSucceedInSingleTurn()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            // Arrange: create 3 files; one is the "chosen" target
            var chosen = Path.Combine(tempRoot, "chosen.txt");
            await File.WriteAllTextAsync(chosen, "chosen-content-xyz");
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "other1.txt"), "other1");
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "other2.txt"), "other2");

            // Single LLM turn emits list_files and read_file back-to-back
            var llmResponse = $"<call>list_files|{tempRoot}</call> <call>read_file|{chosen}</call>";
            var client = new FakeBitNetClient { DefaultResponse = llmResponse };

            var store = new InMemoryEngramStore();
            var identityStore = new InMemoryIdentityStore();
            var registry = new ToolRegistry();
            registry.Register(new ListFilesTool(new[] { tempRoot }));
            registry.Register(new ReadFileTool(new[] { tempRoot }));

            var loop = BuildLoop(client, store, identityStore, registry);

            // Act
            var result = await loop.RunAsync(SensoryEvent.FromText("list files then read one"));

            // Assert
            Assert.Equal(2, result.ToolCalls.Count);
            Assert.Equal("list_files", result.ToolCalls[0].ToolName);
            Assert.Equal("read_file",  result.ToolCalls[1].ToolName);
            Assert.True(result.ToolResults[0].Success, "list_files should succeed");
            Assert.True(result.ToolResults[1].Success, "read_file should succeed");

            // The assembled output includes both observations
            Assert.Contains("<observation>", result.FinalAnswer);
            Assert.Contains("chosen-content-xyz", result.FinalAnswer);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ── Test G: degraded mode when ALL tiers unavailable ─────────────────────

    [Fact]
    public async Task TestG_AllTiersUnavailable_ReturnsDegradedMode()
    {
        // Arrange: mark every tier as unavailable
        var client = new FakeBitNetClient();
        client.UnavailableTiers.Add(ModelTier.Reflex_1B);
        client.UnavailableTiers.Add(ModelTier.Standard_3B);
        client.UnavailableTiers.Add(ModelTier.Deliberate_7B);
        client.UnavailableTiers.Add(ModelTier.DeepReason_10B);

        var store = new InMemoryEngramStore();
        var identityStore = new InMemoryIdentityStore();
        var registry = new ToolRegistry();

        var loop = BuildLoop(client, store, identityStore, registry);

        // Act
        var result = await loop.RunAsync(SensoryEvent.FromText("hello"));

        // Assert
        Assert.True(result.DegradedMode, "DegradedMode should be true when all tiers fail");
        Assert.StartsWith("[bitwyck:", result.FinalAnswer);
        Assert.Empty(result.ToolCalls);
    }
}
