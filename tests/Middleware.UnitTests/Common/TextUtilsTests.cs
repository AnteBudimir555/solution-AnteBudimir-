using Middleware.Core.Common;

namespace Middleware.UnitTests.Common;

/// <summary>
/// Port of the Java <c>TextUtilsTest</c>. Covers the null/within-cap pass-through, the ellipsis
/// budget, word-boundary vs hard-cut behaviour and the invariant that the result never exceeds
/// <c>maxLength</c>.
/// </summary>
public class TextUtilsTests
{
    private const string Ellipsis = "…";

    [Fact]
    public void ReturnsNullForNullInput() =>
        Assert.Null(TextUtils.Truncate(null, 100));

    [Fact]
    public void ReturnsInputUnchangedWhenWithinCap() =>
        Assert.Equal("short", TextUtils.Truncate("short", 100));

    [Fact]
    public void ReturnsInputUnchangedWhenExactlyAtCap()
    {
        const string exactly = "1234567890"; // length 10
        Assert.Equal(exactly, TextUtils.Truncate(exactly, 10));
    }

    [Fact]
    public void BreaksOnWordBoundaryWhenSensible() =>
        // budget = 9, window = "hello wor", last space at index 5 (>= budget/2) -> cut at "hello".
        Assert.Equal("hello" + Ellipsis, TextUtils.Truncate("hello world foo", 10));

    [Fact]
    public void HardCutsWhenNoBoundaryWithinBudget() =>
        // budget = 9, no space in "abcdefghi" -> hard cut then ellipsis.
        Assert.Equal("abcdefghi" + Ellipsis, TextUtils.Truncate("abcdefghijklmno", 10));

    [Fact]
    public void HardCutsWhenWordBoundaryTooEarly() =>
        // budget = 9, window = "ab cdefgh", space at index 2 (< budget/2 = 4) -> hard cut kept.
        Assert.Equal("ab cdefgh" + Ellipsis, TextUtils.Truncate("ab cdefghijkl", 10));

    [Fact]
    public void StripsTrailingWhitespaceBeforeEllipsis() =>
        // Boundary cut lands right after "hello" leaving a trailing space, which must be stripped.
        Assert.Equal("hello" + Ellipsis, TextUtils.Truncate("hello   world", 8));

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void ResultNeverExceedsMaxLength(int maxLength)
    {
        string longText = new string('x', 500) + " and some words here to force a truncation decision";
        string? result = TextUtils.Truncate(longText, maxLength);
        Assert.NotNull(result);
        Assert.True(result!.Length <= maxLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void RejectsNonPositiveMaxLength(int maxLength)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => TextUtils.Truncate("text", maxLength));
        Assert.Contains("maxLength must be positive", ex.Message);
    }
}
