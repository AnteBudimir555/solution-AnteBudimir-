namespace Middleware.Core.Options;

/// <summary>
/// Cache configuration, bound from <c>Cache</c>. Bounds the size and sets the write TTL of the
/// search/filter caches (the .NET analog of the Java Caffeine spec
/// <c>maximumSize=500,expireAfterWrite=60s</c>).
/// </summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    public long MaximumSize { get; set; } = 500;
    public int ExpireAfterWriteSeconds { get; set; } = 60;
}
