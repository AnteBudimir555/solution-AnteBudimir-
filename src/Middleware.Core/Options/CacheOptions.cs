namespace Middleware.Core.Options;

/// <summary>
/// Cache configuration, bound from <c>Cache</c>. Bounds the size and sets the write TTL of the
/// search/filter caches (the .NET analog of the Java Caffeine spec
/// <c>maximumSize=500,expireAfterWrite=60s</c>).
///
/// <para><b>The size bound is expressed in bytes, not entries — a deliberate divergence from the
/// Caffeine spec.</b> Caffeine's <c>maximumSize=500</c> counts entries; the .NET stack has no
/// equivalent. <c>HybridCache</c> sizes each L1 entry by its serialized payload length and
/// <c>MemoryCacheOptions.SizeLimit</c> is compared against the sum of those, so a limit here is a byte
/// budget. Carrying the number 500 across would have read as "500 entries" and meant "500 bytes",
/// which does not throw and does not warn — it silently retains nothing, turning every request into a
/// cache miss while the tests (whose fixtures are small enough to fit) still pass.</para>
/// </summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// Total byte budget for cached entries. The default is 64 MiB, which at a realistic ~100 KiB for a
    /// full 100-product page holds roughly 650 entries — the same order as the Caffeine bound it
    /// replaces, chosen for equivalent capacity rather than an equivalent number.
    /// </summary>
    public long MaximumSizeBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Largest single entry that may be cached. An entry over this is computed and returned normally
    /// but not retained, so one pathological response cannot evict everything else.
    /// </summary>
    public long MaximumEntryBytes { get; set; } = 1024 * 1024;

    public int ExpireAfterWriteSeconds { get; set; } = 60;

    /// <summary>
    /// TTL for the category list, which gets its own because it ages differently from everything else
    /// here: product pages go stale as stock and prices move, whereas the set of categories a source
    /// exposes changes approximately never. The general 60s TTL would have this endpoint hit the
    /// upstream roughly once a minute forever to re-learn the same answer.
    /// </summary>
    public int CategoriesExpireAfterWriteSeconds { get; set; } = 3600;
}
