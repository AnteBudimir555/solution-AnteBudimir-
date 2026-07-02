package com.abysalto.middleware.config;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.NullSource;
import org.junit.jupiter.params.provider.ValueSource;

import java.math.BigDecimal;

import static org.assertj.core.api.Assertions.assertThat;

/**
 * Unit tests for {@link CacheKeys}. The cache correctness contract is: parameter sets that produce
 * the same upstream call must collapse to the same key (case/whitespace, numeric scale, absent
 * bounds), and the two filter-cache key spaces must never collide.
 */
class CacheKeysTest {

    @ParameterizedTest
    @NullSource
    @ValueSource(strings = {"", "   ", "\t\n"})
    void normalizeTextCollapsesNullAndBlankToEmpty(String input) {
        assertThat(CacheKeys.normalizeText(input)).isEmpty();
    }

    @ParameterizedTest
    @CsvSource({"Beauty,beauty", "'  Smartphones  ',smartphones", "HOME-DECOR,home-decor"})
    void normalizeTextTrimsAndLowercases(String input, String expected) {
        assertThat(CacheKeys.normalizeText(input)).isEqualTo(expected);
    }

    @Test
    void searchKeyIncludesNormalizedQueryAndPagination() {
        assertThat(CacheKeys.search("  Phone ", 1, 20)).isEqualTo("search|phone|1|20");
    }

    @Test
    void filterPageKeyIncludesNormalizedCategoryAndPagination() {
        assertThat(CacheKeys.filterPage("  ", 0, 20)).isEqualTo("page||0|20");
    }

    @Test
    void filterCandidatesKeyUsesStarForAbsentBounds() {
        assertThat(CacheKeys.filterCandidates("Beauty", null, null)).isEqualTo("cand|beauty|*|*");
    }

    @Test
    void filterCandidatesKeyNormalizesEquivalentNumericScales() {
        String a = CacheKeys.filterCandidates("beauty", new BigDecimal("10.00"), new BigDecimal("50"));
        String b = CacheKeys.filterCandidates("beauty", new BigDecimal("10"), new BigDecimal("50.0000"));
        assertThat(a).isEqualTo(b).isEqualTo("cand|beauty|10|50");
    }

    @Test
    void filterPageAndFilterCandidatesKeySpacesNeverCollide() {
        // Both live in the same physical cache; the prefixes must keep them disjoint.
        String page = CacheKeys.filterPage("beauty", 0, 20);
        String candidates = CacheKeys.filterCandidates("beauty", null, null);
        assertThat(page).startsWith("page|");
        assertThat(candidates).startsWith("cand|");
        assertThat(page).isNotEqualTo(candidates);
    }
}
