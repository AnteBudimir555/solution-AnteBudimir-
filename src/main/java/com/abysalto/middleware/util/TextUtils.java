package com.abysalto.middleware.util;

/** Small text helpers. */
public final class TextUtils {

    private static final String ELLIPSIS = "…";

    private TextUtils() {
    }

    /**
     * Truncates {@code text} to at most {@code maxLength} characters (TASK §3.1: short description
     * capped at 100 chars). Behaviour:
     * <ul>
     *   <li>{@code null} in → {@code null} out; text already within the cap is returned unchanged.</li>
     *   <li>Otherwise the text is cut so that the result (including a trailing ellipsis) never
     *       exceeds {@code maxLength}. The cut prefers the last word boundary within the budget to
     *       avoid splitting a word; if there is no sensible boundary it falls back to a hard cut.</li>
     * </ul>
     *
     * @param text      the text to truncate (may be {@code null})
     * @param maxLength the maximum length of the returned string; must be positive
     */
    public static String truncate(String text, int maxLength) {
        if (maxLength <= 0) {
            throw new IllegalArgumentException("maxLength must be positive, was " + maxLength);
        }
        if (text == null || text.length() <= maxLength) {
            return text;
        }
        // Reserve one character for the ellipsis so the total stays within maxLength.
        int budget = maxLength - ELLIPSIS.length();
        String window = text.substring(0, budget);
        int lastSpace = window.lastIndexOf(' ');
        // Only break on a word boundary if it does not throw away too much of the text.
        String cut = (lastSpace >= budget / 2) ? window.substring(0, lastSpace) : window;
        return cut.stripTrailing() + ELLIPSIS;
    }
}
