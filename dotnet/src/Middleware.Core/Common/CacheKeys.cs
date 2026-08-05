using System.Globalization;

namespace Middleware.Core.Common;

/// <summary>
/// Builds normalized cache keys for the search/filter caches so parameter sets that are semantically
/// identical map to the same entry: case/whitespace differences in text, equivalent numeric scales
/// (e.g. <c>10</c> vs <c>10.00</c>) and absent price bounds all collapse to one key.
/// <para><see cref="NormalizeText"/> is the single source of truth for free-text normalization: the
/// cache key and the actual upstream call both run the query and category through it, so they can
/// never diverge (two inputs share a key only when they also produce the same upstream call).</para>
/// </summary>
public static class CacheKeys
{
    /// <summary>Normalizes a free-text param (query/category): trimmed and lower-cased; null/blank become "".</summary>
    public static string NormalizeText(string? value) =>
        value is null ? string.Empty : value.Trim().ToLowerInvariant();

    /// <summary>Key for name search: normalized query plus pagination.</summary>
    public static string Search(string? query, int page, int size) =>
        "search|" + NormalizeText(query) + "|" + page + "|" + size;

    /// <summary>Key for the no-price filter path (category listing or full list): normalized category plus pagination.</summary>
    public static string FilterPage(string? category, int page, int size) =>
        "page|" + NormalizeText(category) + "|" + page + "|" + size;

    /// <summary>
    /// Key for the price-filter candidate set: normalized category plus price bounds only —
    /// deliberately independent of pagination, so every page of the same filter reuses one cached
    /// upstream fetch. The <c>page|</c>/<c>cand|</c> prefixes keep the two filter-cache key spaces
    /// from ever colliding.
    /// </summary>
    public static string FilterCandidates(string? category, decimal? minPrice, decimal? maxPrice) =>
        "cand|" + NormalizeText(category) + "|" + Price(minPrice) + "|" + Price(maxPrice);

    // Mirrors BigDecimal.stripTrailingZeros().toPlainString(): fixed-point, no trailing zeros, no
    // exponent. decimal preserves scale (10.00m renders as "10.00"), so strip trailing zeros here.
    private static string Price(decimal? value)
    {
        if (value is null)
        {
            return "*";
        }
        string s = value.Value.ToString(CultureInfo.InvariantCulture);
        if (s.Contains('.'))
        {
            s = s.TrimEnd('0').TrimEnd('.');
        }
        return s;
    }
}
