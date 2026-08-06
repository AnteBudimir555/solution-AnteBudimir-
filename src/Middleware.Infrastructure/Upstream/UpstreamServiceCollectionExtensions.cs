using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Core.Abstractions;
using Middleware.Core.Options;
using Polly;

namespace Middleware.Infrastructure.Upstream;

/// <summary>
/// DI wiring for the DummyJSON upstream adapter: binds <see cref="UpstreamOptions"/> and registers
/// <see cref="DummyJsonProductSource"/> as the <see cref="IProductSource"/>, backed by a typed
/// <see cref="HttpClient"/> carrying a resilience pipeline — retry, circuit breaker, concurrency limit
/// and timeouts — configured from options.
///
/// <para><b>Why the budget is split.</b> The pipeline's total-request timeout is
/// <c>ResponseTimeoutMs</c>, unchanged from before resilience existed, and one attempt gets the
/// shorter <c>AttemptTimeoutMs</c>. Retries therefore fit inside the worst case a client could
/// already experience rather than multiplying it: a dead upstream still reports in ~5s, not ~15s.
/// The cost is that a genuinely slow (rather than failing) upstream gets roughly two and a half
/// attempts before the total timeout cuts it off.</para>
/// </summary>
public static class UpstreamServiceCollectionExtensions
{
    public static IServiceCollection AddUpstreamSource(this IServiceCollection services, IConfiguration configuration)
    {
        // ValidateOnStart, so a bad timeout combination aborts the host naming the configuration key.
        // The resilience pipeline validates the same relationships itself, but only when the typed
        // client is first resolved — during a request, where it reads as a DI fault.
        services.AddOptions<UpstreamOptions>()
            .Bind(configuration.GetSection(UpstreamOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var options = configuration.GetSection(UpstreamOptions.SectionName).Get<UpstreamOptions>() ?? new UpstreamOptions();

        // BaseAddress must end with '/' so the source's relative URIs (e.g. "products/1") resolve under it.
        var baseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

        services.AddHttpClient<IProductSource, DummyJsonProductSource>(client =>
            {
                client.BaseAddress = baseAddress;
                // The pipeline owns every timeout. AddStandardResilienceHandler sets this to
                // InfiniteTimeSpan itself, so this line is belt-and-braces — but an HttpClient.Timeout
                // applied *after* the handler wins over it and would cancel the retry loop on the
                // first attempt, so the intent is stated rather than left to registration order.
                // Pinned by RetriesAreNotCancelledByAClientTimeout.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Separate, shorter budget for establishing the TCP/TLS connection.
                ConnectTimeout = TimeSpan.FromMilliseconds(options.ConnectTimeoutMs),
                AutomaticDecompression = DecompressionMethods.All,
                // Recycle pooled connections so upstream DNS changes are picked up without a restart.
                PooledConnectionLifetime = TimeSpan.FromMinutes(options.PooledConnectionLifetimeMinutes)
            })
            .AddStandardResilienceHandler(pipeline =>
            {
                pipeline.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(options.AttemptTimeoutMs);
                pipeline.TotalRequestTimeout.Timeout = TimeSpan.FromMilliseconds(options.ResponseTimeoutMs);

                // The stock delay is 2s exponential, which spends ~8s before reporting a dead upstream.
                // Every call here is a GET, so retrying is safe; it just has to be cheap enough to fit
                // inside the total budget.
                pipeline.Retry.MaxRetryAttempts = options.RetryAttempts;
                pipeline.Retry.Delay = TimeSpan.FromMilliseconds(options.RetryBaseDelayMs);
                pipeline.Retry.BackoffType = DelayBackoffType.Exponential;
                pipeline.Retry.UseJitter = true;

                pipeline.CircuitBreaker.FailureRatio = options.CircuitBreakerFailureRatio;
                pipeline.CircuitBreaker.MinimumThroughput = options.CircuitBreakerMinimumThroughput;
                pipeline.CircuitBreaker.SamplingDuration = TimeSpan.FromMilliseconds(options.CircuitBreakerSamplingDurationMs);
                pipeline.CircuitBreaker.BreakDuration = TimeSpan.FromMilliseconds(options.CircuitBreakerBreakDurationMs);
            });

        return services;
    }
}
