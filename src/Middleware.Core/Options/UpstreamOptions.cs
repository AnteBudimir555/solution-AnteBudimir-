namespace Middleware.Core.Options;

/// <summary>
/// Configuration for the DummyJSON upstream source, bound from <c>Upstream</c>. Timeouts make a slow
/// or unreachable upstream fail fast (surfacing as a 502) rather than hang the request.
/// <para><see cref="MaxInMemoryCandidates"/> bounds the in-service price-filter fetch (the price
/// filter cannot be pushed down to DummyJSON); exceeding it logs a warning.</para>
/// </summary>
public sealed class UpstreamOptions
{
    public const string SectionName = "Upstream";

    public string BaseUrl { get; set; } = "https://dummyjson.com";
    public int ConnectTimeoutMs { get; set; } = 3000;
    public int ResponseTimeoutMs { get; set; } = 5000;
    public int MaxInMemoryCandidates { get; set; } = 5000;
}
