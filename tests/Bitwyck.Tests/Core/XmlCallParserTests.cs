using Bitwyck.Core.Models;
using Bitwyck.Core.Utilities;

namespace Bitwyck.Tests.Core;

public sealed class XmlCallParserTests
{
    // ── Empty string → empty list ────────────────────────────────────────────

    [Fact]
    public void ParseAll_EmptyString_ReturnsEmptyList()
    {
        var result = XmlCallParser.ParseAll(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseAll_NullString_ReturnsEmptyList()
    {
        // string.IsNullOrEmpty covers null; passing null should return empty.
        var result = XmlCallParser.ParseAll(null!);
        Assert.Empty(result);
    }

    // ── Single call → 1 ToolCall with name + args ────────────────────────────

    [Fact]
    public void ParseAll_SingleCall_ReturnsOneToolCallWithNameAndArgs()
    {
        var text = "<call>read_file|C:/foo/bar.txt</call>";
        var result = XmlCallParser.ParseAll(text);

        Assert.Single(result);
        var call = result[0];
        Assert.Equal("read_file", call.ToolName);
        Assert.Single(call.Arguments);
        Assert.Equal("C:/foo/bar.txt", call.Arguments[0]);
    }

    // ── Multiple calls → list in order ──────────────────────────────────────

    [Fact]
    public void ParseAll_MultipleCalls_ReturnsInOrder()
    {
        var text = "<call>tool_a|arg1</call> some text <call>tool_b|arg2</call> more text <call>tool_c|arg3</call>";
        var result = XmlCallParser.ParseAll(text);

        Assert.Equal(3, result.Count);
        Assert.Equal("tool_a", result[0].ToolName);
        Assert.Equal("tool_b", result[1].ToolName);
        Assert.Equal("tool_c", result[2].ToolName);
    }

    // ── Call with no args → ToolCall with empty args ─────────────────────────

    [Fact]
    public void ParseAll_CallWithNoArgs_ReturnsToolCallWithEmptyArguments()
    {
        var text = "<call>noop</call>";
        var result = XmlCallParser.ParseAll(text);

        Assert.Single(result);
        var call = result[0];
        Assert.Equal("noop", call.ToolName);
        Assert.Empty(call.Arguments);
    }

    // ── Pipe-separated args → list of trimmed strings ────────────────────────

    [Fact]
    public void ParseAll_PipeSeparatedArgs_ReturnsTrimmedArgList()
    {
        var text = "<call>spawn_agent|summarize this paragraph| extra arg </call>";
        var result = XmlCallParser.ParseAll(text);

        Assert.Single(result);
        var call = result[0];
        Assert.Equal("spawn_agent", call.ToolName);
        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal("summarize this paragraph", call.Arguments[0]);
        Assert.Equal("extra arg", call.Arguments[1]);
    }

    [Fact]
    public void ParseAll_ThreePipeArgs_ParsesAllThree()
    {
        var text = "<call>do_thing|a|b|c</call>";
        var result = XmlCallParser.ParseAll(text);

        Assert.Single(result);
        Assert.Equal(3, result[0].Arguments.Count);
        Assert.Equal("a", result[0].Arguments[0]);
        Assert.Equal("b", result[0].Arguments[1]);
        Assert.Equal("c", result[0].Arguments[2]);
    }

    // ── Malformed `<call>incomplete` → empty list ────────────────────────────

    [Fact]
    public void ParseAll_MalformedUnclosedTag_ReturnsEmptyList()
    {
        var text = "<call>incomplete";
        var result = XmlCallParser.ParseAll(text);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseAll_MixedValidAndMalformed_ReturnsOnlyValidCalls()
    {
        // One valid call surrounded by malformed text
        var text = "<call>open tag only <call>valid_tool|arg</call> trailing garbage";
        var result = XmlCallParser.ParseAll(text);

        // The regex only captures complete <call>...</call> blocks.
        Assert.Single(result);
        Assert.Equal("valid_tool", result[0].ToolName);
    }

    // ── Tool name trimming ───────────────────────────────────────────────────

    [Fact]
    public void ParseAll_ToolNameWithWhitespace_TrimsName()
    {
        var text = "<call>  my_tool  |arg1</call>";
        var result = XmlCallParser.ParseAll(text);

        Assert.Single(result);
        Assert.Equal("my_tool", result[0].ToolName);
    }

    // ── RawText is preserved ─────────────────────────────────────────────────

    [Fact]
    public void ParseAll_SingleCall_RawTextMatchesFullElement()
    {
        var raw = "<call>read_file|path.txt</call>";
        var result = XmlCallParser.ParseAll(raw);

        Assert.Single(result);
        Assert.Equal(raw, result[0].RawText);
    }
}
