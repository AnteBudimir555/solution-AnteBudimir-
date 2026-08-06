namespace Middleware.Core.Common;

/// <summary>Small text helpers.</summary>
public static class TextUtils
{
    private const string Ellipsis = "…";

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="maxLength"/> characters (the
    /// short-description cap). Behaviour:
    /// <list type="bullet">
    ///   <item><c>null</c> in → <c>null</c> out; text already within the cap is returned unchanged.</item>
    ///   <item>Otherwise the text is cut so the result (including a trailing ellipsis) never exceeds
    ///   <paramref name="maxLength"/>. The cut prefers the last word boundary within the budget to
    ///   avoid splitting a word; if there is no sensible boundary it falls back to a hard cut.</item>
    ///   <item>A hard cut never splits a surrogate pair: a character straddling the budget is
    ///   dropped whole rather than emitted as a lone surrogate. Truncation therefore never
    ///   introduces one, though a lone surrogate already in <paramref name="text"/> is preserved.</item>
    /// </list>
    /// </summary>
    /// <param name="text">the text to truncate (may be <c>null</c>)</param>
    /// <param name="maxLength">the maximum length of the returned string; must be positive</param>
    public static string? Truncate(string? text, int maxLength)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLength), maxLength, "maxLength must be positive, was " + maxLength);
        }
        if (text is null || text.Length <= maxLength)
        {
            return text;
        }
        // Reserve one character for the ellipsis so the total stays within maxLength.
        int budget = maxLength - Ellipsis.Length;
        // The budget is a UTF-16 index, so a cut can land between the two halves of a surrogate
        // pair — an emoji straddling the boundary would leave an orphaned high surrogate, which is
        // not valid text: System.Text.Json cannot encode it and substitutes U+FFFD, so the client
        // reads back a replacement character. Step back one unit so a split pair is dropped whole.
        // This costs
        // one character of an already-truncated description, and only on the hard-cut path — a
        // boundary cut lands on a space, which is never part of a pair.
        //
        // Note the guarantee is "truncation never *introduces* a lone surrogate", not "the output
        // is always well-formed": a lone surrogate already present in the input is passed through
        // as it always was. Note also that this is a deliberate divergence from the Java original,
        // whose String.substring splits pairs identically — see MIGRATION_PLAN.md L1/T8.
        int windowLength =
            budget > 0 && char.IsHighSurrogate(text[budget - 1]) && char.IsLowSurrogate(text[budget])
                ? budget - 1
                : budget;
        string window = text.Substring(0, windowLength);
        int lastSpace = window.LastIndexOf(' ');
        // Only break on a word boundary if it does not throw away too much of the text.
        string cut = (lastSpace >= budget / 2) ? window.Substring(0, lastSpace) : window;
        return cut.TrimEnd() + Ellipsis;
    }
}
