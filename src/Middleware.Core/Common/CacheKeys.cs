namespace Middleware.Core.Common;

/// <summary>
/// Builds normalized cache keys for the search/filter caches so parameter sets that are semantically
/// identical map to the same entry: case/whitespace differences in text collapse to one key.
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
    /// Key for the price-filter candidate set: normalized category and nothing else — deliberately
    /// independent of both pagination <em>and</em> the price bounds, so every page of every price
    /// range within one category reuses a single cached upstream fetch. The <c>page|</c>/<c>cand|</c>
    /// prefixes keep the two filter-cache key spaces from ever colliding.
    ///
    /// <para>The bounds used to be part of this key. They were removed because they are client-supplied
    /// <c>decimal</c>s: <c>minPrice=10.01</c>, <c>10.02</c>, <c>10.03</c>… each produced a distinct key,
    /// and every one of them missed the cache and pulled the <em>entire</em> upstream catalog, then
    /// retained a copy of it for the TTL. One cheap request amplified into one full catalog fetch plus
    /// one full catalog retained, and single-flight could not help because the keys differed by
    /// construction. Keying on the category alone bounds this cache to one entry per category and makes
    /// the price bounds a pure in-memory filter over an already-cached set (see
    /// <c>ProductQueryCache.PriceFilteredCandidatesAsync</c>), which is where they always belonged —
    /// the upstream call never depended on them.</para>
    /// </summary>
    public static string FilterCandidates(string? category) =>
        "cand|" + NormalizeText(category);

    /// <summary>
    /// Key for the category list. It takes no parameters — there is exactly one such list — so it is a
    /// constant rather than a builder, and it carries a prefix like the rest so the key spaces stay
    /// disjoint.
    /// </summary>
    public const string Categories = "cats|";
}
