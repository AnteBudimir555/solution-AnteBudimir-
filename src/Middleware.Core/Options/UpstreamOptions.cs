using System.ComponentModel.DataAnnotations;

namespace Middleware.Core.Options;

/// <summary>
/// Configuration for the DummyJSON upstream source, bound from <c>Upstream</c>. Timeouts make a slow
/// or unreachable upstream fail fast (surfacing as a 502) rather than hang the request.
/// <para><see cref="MaxInMemoryCandidates"/> bounds the in-service price-filter fetch (the price
/// filter cannot be pushed down to DummyJSON); exceeding it throws rather than allocating without
/// bound.</para>
///
/// <para><b>Two timeouts, and the split matters.</b> <see cref="ResponseTimeoutMs"/> is the budget for
/// the whole call <em>including</em> retries — the worst case a client can wait — while
/// <see cref="AttemptTimeoutMs"/> bounds one HTTP attempt. Retries therefore fit inside the existing
/// budget instead of extending it: adding resilience did not make a dead upstream slower to report.
/// The relationship is validated on start (see <see cref="Validate"/>), because the resilience
/// pipeline otherwise rejects a bad combination on the first request rather than at boot.</para>
/// </summary>
public sealed class UpstreamOptions : IValidatableObject
{
    public const string SectionName = "Upstream";

    public string BaseUrl { get; set; } = "https://dummyjson.com";
    public int ConnectTimeoutMs { get; set; } = 3000;

    /// <summary>Budget for the whole upstream call including retries; the worst case a client waits.</summary>
    public int ResponseTimeoutMs { get; set; } = 5000;

    /// <summary>Budget for a single HTTP attempt. Must be shorter than <see cref="ResponseTimeoutMs"/>.</summary>
    public int AttemptTimeoutMs { get; set; } = 2000;

    public int MaxInMemoryCandidates { get; set; } = 5000;

    /// <summary>Retries after the first attempt. Zero is not accepted by the pipeline — use 1 as the floor.</summary>
    [Range(1, 10)]
    public int RetryAttempts { get; set; } = 2;

    /// <summary>First retry delay; subsequent retries back off exponentially with jitter.</summary>
    [Range(0, 10_000)]
    public int RetryBaseDelayMs { get; set; } = 200;

    /// <summary>
    /// Share of failing attempts inside <see cref="CircuitBreakerSamplingDurationMs"/> that opens the
    /// breaker. Deliberately higher than the library default of 0.1: for a proxy, the occasional
    /// upstream 5xx is weather, not an outage.
    /// </summary>
    [Range(0.1, 1.0)]
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    /// <summary>
    /// Attempts required in the sampling window before the ratio is consulted. The library default is
    /// 100, which a service at this traffic level never reaches — the breaker would be decorative.
    /// </summary>
    [Range(2, 1000)]
    public int CircuitBreakerMinimumThroughput { get; set; } = 20;

    /// <summary>Window over which the failure ratio is measured. Must be at least twice <see cref="AttemptTimeoutMs"/>.</summary>
    public int CircuitBreakerSamplingDurationMs { get; set; } = 30_000;

    /// <summary>How long the breaker stays open, shedding load, before probing the upstream again.</summary>
    [Range(500, 300_000)]
    public int CircuitBreakerBreakDurationMs { get; set; } = 5_000;

    /// <summary>
    /// Caps how long a pooled connection is reused, so the client picks up upstream DNS changes
    /// without a restart. Connections are not torn down mid-request.
    /// </summary>
    [Range(1, 1440)]
    public int PooledConnectionLifetimeMinutes { get; set; } = 5;

    /// <summary>
    /// Checks the relationships the resilience pipeline requires. It enforces these itself, but only
    /// when the typed client is first resolved — which is during a request, so a misconfiguration
    /// would surface as a 500 on the first call and read like a DI fault. Validating here turns it
    /// into a start-up failure that names the configuration key.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AttemptTimeoutMs >= ResponseTimeoutMs)
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(AttemptTimeoutMs)} ({AttemptTimeoutMs}) must be shorter than "
                + $"{SectionName}:{nameof(ResponseTimeoutMs)} ({ResponseTimeoutMs}); the response timeout is the "
                + "budget for the whole call including retries.",
                [nameof(AttemptTimeoutMs), nameof(ResponseTimeoutMs)]);
        }

        if (CircuitBreakerSamplingDurationMs < 2 * AttemptTimeoutMs)
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(CircuitBreakerSamplingDurationMs)} ({CircuitBreakerSamplingDurationMs}) must be "
                + $"at least twice {SectionName}:{nameof(AttemptTimeoutMs)} ({AttemptTimeoutMs}), or the breaker cannot "
                + "observe enough attempts to be effective.",
                [nameof(CircuitBreakerSamplingDurationMs), nameof(AttemptTimeoutMs)]);
        }
    }
}
