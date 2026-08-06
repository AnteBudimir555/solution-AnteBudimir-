using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Core.Abstractions;
using Middleware.Core.Exceptions;
using Middleware.Infrastructure.Upstream;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Middleware.IntegrationTests.Upstream;

/// <summary>
/// Tests for the resilience pipeline on the upstream typed client (MIGRATION_PLAN S1).
///
/// <para>These go through <see cref="UpstreamServiceCollectionExtensions.AddUpstreamSource"/> rather
/// than constructing <see cref="DummyJsonProductSource"/> directly, because the pipeline <em>is</em>
/// the registration — a source built over a bare <see cref="HttpClient"/> (as
/// <see cref="DummyJsonProductSourceTests"/> does, deliberately, to test the mapping in isolation)
/// has no resilience at all. Nothing exercised the wired client before this file existed.</para>
///
/// <para>Every test builds its own container: the circuit breaker is per-pipeline state, and a shared
/// provider would let one test's induced outage open the breaker for the next.</para>
/// </summary>
public sealed class UpstreamResilienceTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    /// <summary>
    /// Builds the real registration against the stub server. Timings are compressed so the suite does
    /// not spend the production budget, but the <em>shape</em> — total &gt; attempt, sampling ≥ 2×
    /// attempt — is the shipped one.
    /// </summary>
    private ServiceProvider BuildProvider(params (string Key, string Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Upstream:BaseUrl"] = _server.Url,
            ["Upstream:ConnectTimeoutMs"] = "1000",
            ["Upstream:ResponseTimeoutMs"] = "5000",
            ["Upstream:AttemptTimeoutMs"] = "1000",
            ["Upstream:RetryAttempts"] = "2",
            ["Upstream:RetryBaseDelayMs"] = "1",
            ["Upstream:CircuitBreakerSamplingDurationMs"] = "30000",
            ["Upstream:CircuitBreakerBreakDurationMs"] = "60000"
        };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUpstreamSource(configuration);
        return services.BuildServiceProvider();
    }

    private static IProductSource SourceFrom(ServiceProvider provider) =>
        provider.GetRequiredService<IProductSource>();

    private void StubStatus(string path, int statusCode) =>
        _server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(statusCode));

    private int Hits(string path) =>
        _server.LogEntries.Count(entry => entry.RequestMessage?.Path == path);

    private const string ProductsJson =
        """{"products":[],"total":0,"skip":0,"limit":30}""";

    [Fact]
    public async Task ATransientUpstreamFailureIsRetriedAndTheCallSucceeds()
    {
        // First call 500, every call after it 200 — the "single DummyJSON blip" S1 is about.
        _server.Given(Request.Create().WithPath("/products").UsingGet())
            .InScenario("blip").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(500));
        _server.Given(Request.Create().WithPath("/products").UsingGet())
            .InScenario("blip").WhenStateIs("recovered")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(ProductsJson));

        await using var provider = BuildProvider();

        var page = await SourceFrom(provider).ListAsync(0, 30);

        Assert.Equal(0, page.Total);
        // Two upstream calls for one logical call: the retry is the point, and it is not free.
        Assert.Equal(2, Hits("/products"));
    }

    [Fact]
    public async Task APersistentUpstreamFailureIsRetriedToTheConfiguredLimitAndThenSurfaces()
    {
        StubStatus("/products", 503);

        await using var provider = BuildProvider(("Upstream:RetryAttempts", "2"));

        await Assert.ThrowsAsync<UpstreamException>(() => SourceFrom(provider).ListAsync(0, 30));

        // One initial attempt plus RetryAttempts retries, and no more: the retry count is a bound,
        // not a suggestion, so a failing upstream is amplified by a known factor.
        Assert.Equal(3, Hits("/products"));
    }

    [Fact]
    public async Task ANotFoundIsNotRetried()
    {
        StubStatus("/products/999", 404);

        await using var provider = BuildProvider();

        await Assert.ThrowsAsync<ProductNotFoundException>(() => SourceFrom(provider).GetByIdAsync(999));

        // 404 is an answer, not a failure. Retrying it would triple the upstream cost of every
        // request for a product that does not exist — which a client can issue at will.
        Assert.Equal(1, Hits("/products/999"));
    }

    [Fact]
    public void RetriesAreNotCancelledByAClientTimeout()
    {
        // MIGRATION_PLAN T3. On this version AddStandardResilienceHandler sets Timeout itself, so the
        // assertion below holds even without the explicit line in the registration — but a
        // ConfigureHttpClient applied *after* the handler overrides it and silently caps the whole
        // pipeline at one attempt. This pins the invariant rather than the mechanism.
        using var provider = BuildProvider();

        // AddHttpClient<TClient, TImplementation> names the registration after TClient.
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IProductSource));

        // Guards against naming the wrong registration: an unknown name yields a default client, and
        // a default client would pass neither of these.
        Assert.Equal(new Uri(_server.Url!.TrimEnd('/') + "/"), client.BaseAddress);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public async Task RetriesRunPastTheDurationOfASingleAttempt()
    {
        // The behavioural half of T3: three attempts against an upstream slow enough that a
        // per-attempt-sized overall timeout would have cut the sequence short.
        _server.Given(Request.Create().WithPath("/products").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithDelay(TimeSpan.FromMilliseconds(300)));

        await using var provider = BuildProvider(
            ("Upstream:AttemptTimeoutMs", "600"),
            ("Upstream:ResponseTimeoutMs", "5000"),
            ("Upstream:CircuitBreakerSamplingDurationMs", "5000"));

        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<UpstreamException>(() => SourceFrom(provider).ListAsync(0, 30));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(3, Hits("/products"));
        Assert.True(elapsed > TimeSpan.FromMilliseconds(600),
            $"expected the retry sequence to outlast one 600ms attempt, took {elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task TheCircuitBreakerOpensAndShedsLoadWithoutReachingTheUpstream()
    {
        StubStatus("/products", 500);

        // Retries feed the breaker too — each attempt is recorded — so a low throughput threshold is
        // reached quickly. The break duration is long so the test cannot race a half-open probe.
        await using var provider = BuildProvider(
            ("Upstream:CircuitBreakerMinimumThroughput", "4"),
            ("Upstream:CircuitBreakerFailureRatio", "0.5"));
        var source = SourceFrom(provider);

        var previousHits = 0;
        var hitsWhenBroken = -1;
        for (var i = 0; i < 40; i++)
        {
            await Assert.ThrowsAsync<UpstreamException>(() => source.ListAsync(0, 30));

            var hits = Hits("/products");
            // The first request that adds no upstream call is the first one the breaker shed.
            if (hitsWhenBroken < 0 && i > 0 && hits == previousHits)
            {
                hitsWhenBroken = hits;
            }
            previousHits = hits;
        }

        Assert.True(hitsWhenBroken > 0,
            $"the breaker never opened: all 40 requests reached the upstream ({Hits("/products")} calls)");
        // Once open it stays open for the break duration: later requests fail without any upstream call.
        Assert.Equal(hitsWhenBroken, Hits("/products"));
    }

    [Fact]
    public async Task AnUnreachableUpstreamSurfacesAsAnUpstreamException()
    {
        var deadServer = WireMockServer.Start();
        var deadUrl = deadServer.Url!;
        deadServer.Stop();

        await using var provider = BuildProvider(
            ("Upstream:BaseUrl", deadUrl),
            ("Upstream:ConnectTimeoutMs", "500"),
            ("Upstream:AttemptTimeoutMs", "800"),
            ("Upstream:ResponseTimeoutMs", "4000"));

        await Assert.ThrowsAsync<UpstreamException>(() => SourceFrom(provider).ListAsync(0, 30));

        deadServer.Dispose();
    }

    public void Dispose() => _server.Dispose();
}
