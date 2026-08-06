using System.Text.Json;
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

    // --- Surrogate-pair handling (L1). The hard-cut path cuts at a UTF-16 index, so a character
    // outside the BMP straddling the budget used to be halved. ---

    /// <summary>U+1F600 GRINNING FACE — two UTF-16 units (D83D DE00).</summary>
    private const string Emoji = "\U0001F600";

    [Fact]
    public void HardCutDropsAStraddlingSurrogatePairWholeRatherThanSplittingIt()
    {
        // budget = 9, no space -> hard cut; the emoji occupies indices 8-9, so the cut lands
        // between its halves. The whole character is dropped, costing one unit of the budget.
        string? result = TextUtils.Truncate("abcdefgh" + Emoji + "ijklmnop", 10);

        Assert.Equal("abcdefgh" + Ellipsis, result);
        Assert.DoesNotContain(result!, char.IsSurrogate);
    }

    [Fact]
    public void KeepsASurrogatePairThatFitsEntirelyWithinTheBudget()
    {
        // budget = 9, the emoji occupies indices 7-8 and fits -> it must survive intact.
        string? result = TextUtils.Truncate("abcdefg" + Emoji + "hijklmno", 10);

        Assert.Equal("abcdefg" + Emoji + Ellipsis, result);
        Assert.Equal(10, result!.Length);
    }

    [Fact]
    public void TruncatedTextRoundTripsThroughJsonWithoutAReplacementCharacter()
    {
        // The observable symptom: System.Text.Json cannot encode a lone surrogate and substitutes
        // U+FFFD, so a split pair reaches the client as a replacement character.
        string? result = TextUtils.Truncate(new string('x', 98) + Emoji + " tail", 100);

        string json = JsonSerializer.Serialize(new { description = result });
        string? roundTripped = JsonSerializer.Deserialize<JsonElement>(json)
            .GetProperty("description").GetString();

        Assert.Equal(result, roundTripped);
        Assert.DoesNotContain('�', roundTripped!);
    }

    [Fact]
    public void ALoneSurrogateAlreadyPresentInTheInputIsPassedThrough()
    {
        // The guarantee is that truncation never *introduces* a lone surrogate, not that it
        // repairs malformed input — that would be a different (and lossier) contract.
        string malformed = "abc\uD83Ddefghijklmno";

        string? result = TextUtils.Truncate(malformed, 10);

        Assert.Equal("abc\uD83Ddefgh" + Ellipsis, result);
    }

    [Fact]
    public void WordBoundaryCutsAreUnaffectedBySurrogates()
    {
        // A boundary cut lands on a space, which is never half of a pair; the emoji is simply
        // beyond the cut. Pins that the surrogate guard did not perturb this path.
        Assert.Equal("hello" + Ellipsis, TextUtils.Truncate("hello wo" + Emoji + "rld and more", 12));
    }

    [Fact]
    public void HandlesAPairStraddlingTheSmallestUsableBudget()
    {
        // maxLength = 2 -> budget = 1, which is the emoji's leading half alone; dropping it leaves
        // nothing but the ellipsis. Guards the index arithmetic at the low end.
        Assert.Equal(Ellipsis, TextUtils.Truncate(Emoji + "abc", 2));
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
