using System.Runtime.CompilerServices;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Tooling;
using Bitwyck.Tests.Fakes;

namespace Bitwyck.Tests.Tooling;

public class XmlToolInterceptorTests
{
    private static XmlToolInterceptor.ToolHandler EchoHandler =>
        (call, _) => Task.FromResult(ToolResult.Ok(call.ToolName, $"echo:{call.Arguments[0]}"));

    private static async IAsyncEnumerable<InferenceTokenChunk> ToStream(
        string text,
        int chunkSize = 8,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = text.Substring(i, Math.Min(chunkSize, text.Length - i));
            bool isFinal = i + chunkSize >= text.Length;
            yield return new InferenceTokenChunk(slice, isFinal);
            await Task.Yield();
        }
        if (text.Length == 0)
            yield return new InferenceTokenChunk(string.Empty, true);
    }

    // ── No tags ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoTags_AssembledOutputEqualsConcatenatedTokens()
    {
        var interceptor = new XmlToolInterceptor();
        var text = "Hello, world! No calls here.";
        int handlerCallCount = 0;

        var result = await interceptor.ProcessAsync(
            ToStream(text),
            (call, ct) => { handlerCallCount++; return Task.FromResult(ToolResult.Ok(call.ToolName, "")); });

        Assert.Equal(text, result.AssembledOutput);
        Assert.Empty(result.ToolCalls);
        Assert.Empty(result.ToolResults);
        Assert.Equal(0, handlerCallCount);
    }

    // ── Single complete call ───────────────────────────────────────────────────

    [Fact]
    public async Task SingleCompleteCall_HandlerInvokedOnce_OutputHasObservation()
    {
        var interceptor = new XmlToolInterceptor();
        var text = "before <call>read_file|x.txt</call> after";
        var capturedCalls = new List<ToolCall>();

        var result = await interceptor.ProcessAsync(
            ToStream(text),
            (call, ct) =>
            {
                capturedCalls.Add(call);
                return Task.FromResult(ToolResult.Ok(call.ToolName, "file-content"));
            });

        Assert.Single(capturedCalls);
        Assert.Equal("read_file", capturedCalls[0].ToolName);
        Assert.Equal("x.txt", capturedCalls[0].Arguments[0]);
        Assert.Single(result.ToolCalls);
        Assert.Contains("<observation>file-content</observation>", result.AssembledOutput);
        Assert.Contains("before ", result.AssembledOutput);
        Assert.Contains(" after", result.AssembledOutput);
    }

    // ── Tag split across many tiny chunks ────────────────────────────────────

    [Fact]
    public async Task TagSplitAcrossSingleCharChunks_StillDetected()
    {
        var interceptor = new XmlToolInterceptor();
        // StreamChunkSize=1 forces character-by-character streaming
        var fake = new FakeBitNetClient { StreamChunkSize = 1 };
        fake.DefaultResponse = "pre<call>read_file|y.txt</call>post";

        var request = new InferenceRequest(
            Tier: ModelTier.Reflex_1B,
            Messages: new[] { new InferenceMessage(MessageRole.User, "go") },
            MaxTokens: 64,
            Temperature: 0.0,
            TopP: 1.0);

        var capturedCalls = new List<ToolCall>();
        var result = await interceptor.ProcessAsync(
            fake.StreamAsync(request),
            (call, ct) =>
            {
                capturedCalls.Add(call);
                return Task.FromResult(ToolResult.Ok(call.ToolName, "single-char-ok"));
            });

        Assert.Single(capturedCalls);
        Assert.Equal("read_file", capturedCalls[0].ToolName);
        Assert.Equal("y.txt", capturedCalls[0].Arguments[0]);
        Assert.Contains("<observation>single-char-ok</observation>", result.AssembledOutput);
    }

    // ── Multiple calls in one stream ─────────────────────────────────────────

    [Fact]
    public async Task MultipleCalls_AllDetectedInOrder()
    {
        var interceptor = new XmlToolInterceptor();
        var text = "<call>tool_a|arg1</call> middle <call>tool_b|arg2</call>";

        var result = await interceptor.ProcessAsync(
            ToStream(text, chunkSize: 4),
            (call, ct) => Task.FromResult(ToolResult.Ok(call.ToolName, $"result-{call.ToolName}")));

        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal("tool_a", result.ToolCalls[0].ToolName);
        Assert.Equal("tool_b", result.ToolCalls[1].ToolName);
        Assert.Contains("<observation>result-tool_a</observation>", result.AssembledOutput);
        Assert.Contains("<observation>result-tool_b</observation>", result.AssembledOutput);
    }

    // ── Handler returns Fail ──────────────────────────────────────────────────

    [Fact]
    public async Task HandlerReturnsFail_ObservationHasErrorAttribute()
    {
        var interceptor = new XmlToolInterceptor();
        var text = "<call>bad_tool|bad_arg</call>";

        var result = await interceptor.ProcessAsync(
            ToStream(text),
            (call, ct) => Task.FromResult(ToolResult.Fail(call.ToolName, "something went wrong")));

        Assert.Single(result.ToolCalls);
        Assert.Contains("<observation error=\"true\">something went wrong</observation>",
            result.AssembledOutput);
    }

    // ── Stream ends mid-tag ───────────────────────────────────────────────────

    [Fact]
    public async Task StreamEndsMidTag_PassesThroughUnchanged_NoCallsDetected()
    {
        var interceptor = new XmlToolInterceptor();
        var text = "some text <call>incomp";

        var result = await interceptor.ProcessAsync(
            ToStream(text),
            (call, ct) => Task.FromResult(ToolResult.Ok(call.ToolName, "should not happen")));

        Assert.Empty(result.ToolCalls);
        Assert.Contains("<call>incomp", result.AssembledOutput);
        Assert.Contains("some text ", result.AssembledOutput);
    }
}
