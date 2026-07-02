package com.abysalto.middleware.util;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.ValueSource;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

/**
 * Unit tests for {@link TextUtils#truncate} — the short-description cap (TASK §3.1). Covers the
 * null/within-cap pass-through, the ellipsis budget, word-boundary vs hard-cut behaviour and the
 * invariant that the result never exceeds {@code maxLength}.
 */
class TextUtilsTest {

    private static final char ELLIPSIS = '…';

    @Test
    void returnsNullForNullInput() {
        assertThat(TextUtils.truncate(null, 100)).isNull();
    }

    @Test
    void returnsInputUnchangedWhenWithinCap() {
        assertThat(TextUtils.truncate("short", 100)).isEqualTo("short");
    }

    @Test
    void returnsInputUnchangedWhenExactlyAtCap() {
        String exactly = "1234567890"; // length 10
        assertThat(TextUtils.truncate(exactly, 10)).isEqualTo(exactly);
    }

    @Test
    void breaksOnWordBoundaryWhenSensible() {
        // budget = 9, window = "hello wor", last space at index 5 (>= budget/2) -> cut at "hello".
        assertThat(TextUtils.truncate("hello world foo", 10)).isEqualTo("hello" + ELLIPSIS);
    }

    @Test
    void hardCutsWhenNoBoundaryWithinBudget() {
        // budget = 9, no space in "abcdefghi" -> hard cut then ellipsis.
        assertThat(TextUtils.truncate("abcdefghijklmno", 10)).isEqualTo("abcdefghi" + ELLIPSIS);
    }

    @Test
    void hardCutsWhenWordBoundaryTooEarly() {
        // budget = 9, window = "ab cdefgh", space at index 2 (< budget/2 = 4) -> hard cut kept.
        assertThat(TextUtils.truncate("ab cdefghijkl", 10)).isEqualTo("ab cdefgh" + ELLIPSIS);
    }

    @Test
    void stripsTrailingWhitespaceBeforeEllipsis() {
        // Boundary cut lands right after "hello" leaving a trailing space, which must be stripped.
        assertThat(TextUtils.truncate("hello   world", 8)).isEqualTo("hello" + ELLIPSIS);
    }

    @ParameterizedTest
    @ValueSource(ints = {10, 20, 50, 100})
    void resultNeverExceedsMaxLength(int maxLength) {
        String longText = "x".repeat(500) + " and some words here to force a truncation decision";
        assertThat(TextUtils.truncate(longText, maxLength)).hasSizeLessThanOrEqualTo(maxLength);
    }

    @ParameterizedTest
    @CsvSource({"0", "-1", "-100"})
    void rejectsNonPositiveMaxLength(int maxLength) {
        assertThatThrownBy(() -> TextUtils.truncate("text", maxLength))
                .isInstanceOf(IllegalArgumentException.class)
                .hasMessageContaining("maxLength must be positive");
    }
}
