using System.Globalization;

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
    /// <summary>
    /// Normalizes a free-text param (query/category): trimmed and lower-cased; null/blank become "".
    /// <para><c>ToLowerInvariant</c> rather than <c>ToLower</c> is load-bearing, not stylistic: the
    /// culture-sensitive overload maps <c>"ISTANBUL"</c> to <c>"ıstanbul"</c> under <c>tr-TR</c>, so the
    /// same request would produce a different cache key — and a different upstream call — depending on
    /// the host's locale. Because this value is passed to the source as well as into the key (see
    /// <see cref="Abstractions.IProductSource"/>'s free-text convention), that would be a behaviour
    /// change, not just a key change.</para>
    /// </summary>
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
    /// <c>ProductQueryCache</c>'s in-memory price path), which is where they always belonged — the
    /// upstream call never depended on them.</para>
    ///
    /// <para>That reasoning holds only while the source cannot filter on price itself. One that can is
    /// asked for a filtered page directly and keyed by <see cref="PricePage"/> instead, because then
    /// the bounds <em>do</em> change the call.</para>
    /// </summary>
    public static string FilterCandidates(string? category) =>
        "cand|" + NormalizeText(category);

    /// <summary>
    /// Key for a price-filtered page that the <em>source</em> filtered, used only when
    /// <c>IProductSource.SupportsPriceFilter</c> is true.
    ///
    /// <para>This is the one key that does carry the price bounds, and it has to: when the source
    /// applies them, the answer depends on them, and <see cref="FilterCandidates"/>'s category-only key
    /// would serve one range's results for another — the exact cross-contamination that key shape was
    /// designed to make impossible.</para>
    ///
    /// <para><strong>The cost is stated rather than hidden: this key space is client-controlled and
    /// unbounded</strong>, since <c>minPrice=10.01, 10.02, …</c> are genuinely different questions
    /// here. What makes that acceptable is precisely the capability that puts a caller on this path —
    /// a miss costs one indexed query at the source, not the full-catalog download that made the same
    /// key shape a release blocker for DummyJSON. Entry count is bounded by
    /// <c>Cache:MaximumSizeBytes</c>, so the residual risk is cache churn rather than upstream
    /// amplification. A source with an expensive price filter should not declare the capability.</para>
    /// </summary>
    public static string PricePage(string? category, decimal? minPrice, decimal? maxPrice, int page, int size) =>
        "pricepage|" + NormalizeText(category) + "|" + Price(minPrice) + "|" + Price(maxPrice)
        + "|" + page + "|" + size;

    /// <summary>
    /// Renders a price bound for a key: invariant, and with trailing-zero scale collapsed so that
    /// <c>10</c> and <c>10.00</c> — equal as <c>decimal</c>s, distinct as strings — cannot become two
    /// entries for one question. An absent bound is empty, which is distinct from <c>0</c>.
    /// </summary>
    private static string Price(decimal? value) =>
        value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Key for the category list. It takes no parameters — there is exactly one such list — so it is a
    /// constant rather than a builder, and it carries a prefix like the rest so the key spaces stay
    /// disjoint.
    /// </summary>
    public const string Categories = "cats|";
}
