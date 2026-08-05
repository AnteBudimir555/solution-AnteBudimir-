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
        string window = text.Substring(0, budget);
        int lastSpace = window.LastIndexOf(' ');
        // Only break on a word boundary if it does not throw away too much of the text.
        string cut = (lastSpace >= budget / 2) ? window.Substring(0, lastSpace) : window;
        return cut.TrimEnd() + Ellipsis;
    }
}
