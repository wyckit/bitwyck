using Bitwyck.Runtime.Lifecycle;

namespace Bitwyck.Tests.Lifecycle;

public class CronExpressionTests
{
    // ── Wildcard matches any time ─────────────────────────────────────────────

    [Fact]
    public void StarStar_MatchesAnyTime()
    {
        var expr = CronExpression.Parse("* * * * *");
        var now = DateTimeOffset.UtcNow;

        Assert.True(expr.Matches(now));
    }

    // ── Hour-pinned expression ────────────────────────────────────────────────

    [Fact]
    public void HourPinned_Matches_0900_NotOther()
    {
        var expr = CronExpression.Parse("0 9 * * *");

        var match = new DateTimeOffset(2025, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var noMatch = new DateTimeOffset(2025, 6, 1, 9, 1, 0, TimeSpan.Zero);

        Assert.True(expr.Matches(match));
        Assert.False(expr.Matches(noMatch));
    }

    // ── Step expression every 15 minutes ─────────────────────────────────────

    [Theory]
    [InlineData(0,  true)]
    [InlineData(15, true)]
    [InlineData(30, true)]
    [InlineData(45, true)]
    [InlineData(1,  false)]
    [InlineData(14, false)]
    [InlineData(16, false)]
    [InlineData(59, false)]
    public void EveryFifteenMinutes_MatchesOnly_0_15_30_45(int minute, bool expected)
    {
        var expr = CronExpression.Parse("*/15 * * * *");
        var dt = new DateTimeOffset(2025, 3, 5, 10, minute, 0, TimeSpan.Zero);

        Assert.Equal(expected, expr.Matches(dt));
    }

    // ── Business hours weekday vs. weekend ───────────────────────────────────

    [Fact]
    public void BusinessHours_WeekdayAt9AM_Matches()
    {
        var expr = CronExpression.Parse("0 9-17 * * 1-5");
        // May 5 2025 = Monday
        var weekday9am = new DateTimeOffset(2025, 5, 5, 9, 0, 0, TimeSpan.Zero);

        Assert.True(expr.Matches(weekday9am));
    }

    [Fact]
    public void BusinessHours_WeekendAt9AM_DoesNotMatch()
    {
        var expr = CronExpression.Parse("0 9-17 * * 1-5");
        // May 10 2025 = Saturday
        var saturday9am = new DateTimeOffset(2025, 5, 10, 9, 0, 0, TimeSpan.Zero);

        Assert.False(expr.Matches(saturday9am));
    }

    // ── Bad input throws FormatException ─────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]          // Only 4 fields
    [InlineData("abc * * * *")]      // Non-numeric minute
    public void BadInput_ThrowsFormatException(string bad)
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse(bad));
    }

    [Fact]
    public void OutOfRangeMinute_ThrowsFormatException()
    {
        // Minute field allows 0-59; 60 is out of range
        Assert.Throws<FormatException>(() => CronExpression.Parse("60 * * * *"));
    }

    // ── Next() returns strictly-greater matching instant ─────────────────────

    [Fact]
    public void Next_ReturnsStrictlyGreaterDateTimeOffsetThatMatches()
    {
        var expr = CronExpression.Parse("* * * * *");
        var from = new DateTimeOffset(2025, 5, 1, 12, 0, 0, TimeSpan.Zero);

        var next = expr.Next(from);

        Assert.True(next > from, "Next() must be strictly after 'from'");
        Assert.True(expr.Matches(next), "Next() result must match the expression");
    }

    [Fact]
    public void Next_PinnedHour_ReturnsCorrectNextFire()
    {
        var expr = CronExpression.Parse("0 9 * * *");
        // 09:01 — next fire is tomorrow 09:00
        var from = new DateTimeOffset(2025, 5, 1, 9, 1, 0, TimeSpan.Zero);

        var next = expr.Next(from);

        Assert.True(next > from);
        Assert.True(expr.Matches(next));
        Assert.Equal(9, next.Hour);
        Assert.Equal(0, next.Minute);
    }
}
