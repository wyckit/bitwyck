using Bitwyck.Core.Utilities;

namespace Bitwyck.Tests.Core;

public sealed class TokenBudgetTests
{
    // ── Estimate(string) ─────────────────────────────────────────────────────

    [Fact]
    public void Estimate_EmptyString_ReturnsZero()
    {
        Assert.Equal(0, TokenBudget.Estimate(string.Empty));
    }

    [Fact]
    public void Estimate_NullString_ReturnsZero()
    {
        string? nullStr = null;
        Assert.Equal(0, TokenBudget.Estimate(nullStr!));
    }

    [Theory]
    [InlineData("abcd", 1)]      // exactly 4 bytes → ceil(4/4) = 1
    [InlineData("abcde", 2)]     // 5 bytes → ceil(5/4) = 2
    [InlineData("abcdefgh", 2)]  // 8 bytes → ceil(8/4) = 2
    [InlineData("abcdefghi", 3)] // 9 bytes → ceil(9/4) = 3
    public void Estimate_AsciiText_ReturnsExpectedTokenCount(string text, int expected)
    {
        Assert.Equal(expected, TokenBudget.Estimate(text));
    }

    [Fact]
    public void Estimate_SingleChar_ReturnsOne()
    {
        // 1 byte → ceil(1/4) = 1
        Assert.Equal(1, TokenBudget.Estimate("x"));
    }

    // ── Estimate(IEnumerable<string>) ────────────────────────────────────────

    [Fact]
    public void Estimate_EmptyEnumerable_ReturnsZero()
    {
        Assert.Equal(0, TokenBudget.Estimate(Array.Empty<string>()));
    }

    [Fact]
    public void Estimate_MultipleStrings_SumsEachEstimate()
    {
        // "abcd" = 1, "efgh" = 1, "ijkl" = 1 → total = 3
        var texts = new[] { "abcd", "efgh", "ijkl" };
        Assert.Equal(3, TokenBudget.Estimate(texts));
    }

    [Fact]
    public void Estimate_ListWithEmptyString_IgnoresEmpty()
    {
        // "abcd" = 1, "" = 0 → total = 1
        var texts = new[] { "abcd", string.Empty };
        Assert.Equal(1, TokenBudget.Estimate(texts));
    }

    [Fact]
    public void Estimate_Enumerable_MatchesSumOfIndividualEstimates()
    {
        var texts = new[] { "hello world", "this is a test", "foo bar baz" };
        var expected = texts.Sum(t => TokenBudget.Estimate(t));
        Assert.Equal(expected, TokenBudget.Estimate(texts));
    }

    // ── Truncate ─────────────────────────────────────────────────────────────

    [Fact]
    public void Truncate_FitsBudget_ReturnsPrefixOfUpToMaxTokens()
    {
        // "abcdef" = 6 bytes; maxTokens=1 → allowedBytes=4 → "abcd"
        var result = TokenBudget.Truncate("abcdef", 1);
        Assert.Equal("abcd", result);
    }

    [Fact]
    public void Truncate_ZeroMaxTokens_ReturnsEmpty()
    {
        var result = TokenBudget.Truncate("abcdef", 0);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Truncate_HugeBudget_ReturnsFullText()
    {
        var text = "This is a normal-length sentence with some words in it.";
        var result = TokenBudget.Truncate(text, 100_000);
        Assert.Equal(text, result);
    }

    [Fact]
    public void Truncate_EmptyString_ReturnsEmpty()
    {
        var result = TokenBudget.Truncate(string.Empty, 10);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Truncate_TextExactlyFits_ReturnsFullText()
    {
        // "abcd" = 4 bytes = exactly 1 token
        var result = TokenBudget.Truncate("abcd", 1);
        Assert.Equal("abcd", result);
    }

    [Fact]
    public void Truncate_NegativeBudget_ReturnsEmpty()
    {
        var result = TokenBudget.Truncate("some text", -5);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Truncate_LongText_ResultFitsWithinBudget()
    {
        var longText = new string('a', 1000); // 1000 ASCII bytes
        var maxTokens = 10;
        var result = TokenBudget.Truncate(longText, maxTokens);
        // Result must fit within budget
        Assert.True(TokenBudget.Estimate(result) <= maxTokens);
        // Result should not be empty
        Assert.NotEmpty(result);
    }
}
