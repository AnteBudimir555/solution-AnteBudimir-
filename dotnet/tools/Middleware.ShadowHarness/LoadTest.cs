using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Middleware.ShadowHarness;

/// <summary>
/// Compares the two services under concurrent load on the two things that actually matter for a
/// caching proxy: how long it takes to answer, and how much upstream traffic it costs.
///
/// <para>Both services are pointed at their own <see cref="UpstreamCounter"/>, so upstream calls are
/// counted rather than inferred, and both proxies forward to the same origin and add the same hop.
/// Three phases run against each service in turn:</para>
/// <list type="number">
///   <item><b>Cold single-flight.</b> A query no run has used before, requested by every worker at
///   once. A cache with stampede protection answers all of them from one upstream call; one without
///   makes as many calls as there are workers. This is Spring's <c>@Cacheable(sync = true)</c> against
///   HybridCache's <c>GetOrCreateAsync</c>, measured.</item>
///   <item><b>Warm throughput.</b> The same query, now cached: latency here is the service's own
///   overhead with the upstream out of the picture, and the upstream count must stay at zero.</item>
///   <item><b>Mixed corpus.</b> A spread of endpoints and filters, which is where paging a
///   price-filtered result must reuse one catalog fetch across pages rather than re-fetching per
///   page.</item>
/// </list>
/// </summary>
internal static class LoadTest
{
    public static async Task<int> RunAsync(HarnessOptions options)
    {
        Console.WriteLine($"Load: {options.LoadConcurrency} concurrent workers, "
                          + $"{options.LoadRequests} requests per throughput phase");
        Console.WriteLine($"Upstream counters on 127.0.0.1:{options.JavaUpstreamPort} (java) and "
                          + $"127.0.0.1:{options.DotnetUpstreamPort} (dotnet), forwarding to {options.UpstreamOrigin}");
        Console.WriteLine();

        using var javaUpstream = new UpstreamCounter(options.JavaUpstreamPort, options.UpstreamOrigin);
        using var dotnetUpstream = new UpstreamCounter(options.DotnetUpstreamPort, options.UpstreamOrigin);
        javaUpstream.Start();
        dotnetUpstream.Start();

        // A term no earlier run can have cached, so the cold phase is genuinely cold on both sides.
        var coldTerm = "load" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

        var results = new List<PhaseResult>();
        foreach (var (label, baseUrl, counter) in new[]
                 {
                     ("java", options.JavaBaseUrl, javaUpstream),
                     ("dotnet", options.DotnetBaseUrl, dotnetUpstream)
                 })
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(baseUrl, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(60)
            };

            string token;
            try
            {
                token = await LoginAsync(client, options);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                Console.Error.WriteLine($"Could not authenticate against the {label} service at {baseUrl}: {ex.Message}");
                return 2;
            }

            results.AddRange(await RunPhasesAsync(label, client, token, counter, coldTerm, options));
        }

        var report = Render(options, results);
        await File.WriteAllTextAsync(options.LoadOutputPath, report);

        Console.WriteLine();
        Console.WriteLine($"Report written to {options.LoadOutputPath}");

        // The single-flight guarantee is the one hard assertion here: a regression makes the service
        // hammer the upstream under load, which no latency number would necessarily reveal.
        var singleFlight = results.Where(r => r.Phase == Phase.ColdSingleFlight).ToList();
        var broken = singleFlight.Where(r => r.UpstreamCalls != 1).ToList();
        foreach (var failure in broken)
        {
            Console.Error.WriteLine($"{failure.Service}: {options.LoadConcurrency} concurrent identical requests "
                                    + $"cost {failure.UpstreamCalls} upstream calls, expected 1.");
        }

        return broken.Count == 0 ? 0 : 1;
    }

    private enum Phase
    {
        ColdSingleFlight,
        WarmThroughput,
        MixedCorpus
    }

    private sealed record PhaseResult(
        string Service,
        Phase Phase,
        int Requests,
        int UpstreamCalls,
        double WallClockMs,
        IReadOnlyList<double> Latencies,
        int NonSuccess)
    {
        public double Percentile(double fraction)
        {
            if (Latencies.Count == 0)
            {
                return 0;
            }
            var ordered = Latencies.Order().ToList();
            var index = (int)Math.Ceiling(fraction * ordered.Count) - 1;
            return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
        }

        public double RequestsPerSecond => WallClockMs <= 0 ? 0 : Requests * 1000.0 / WallClockMs;
    }

    private static async Task<List<PhaseResult>> RunPhasesAsync(
        string label, HttpClient client, string token, UpstreamCounter counter, string coldTerm, HarnessOptions options)
    {
        var results = new List<PhaseResult>();

        counter.Reset();
        var cold = await DriveAsync(label, Phase.ColdSingleFlight, client, token,
            Enumerable.Repeat($"/api/products/search?q={coldTerm}", options.LoadConcurrency).ToList(),
            options.LoadConcurrency, counter);
        results.Add(cold);
        Report(cold);

        counter.Reset();
        var warm = await DriveAsync(label, Phase.WarmThroughput, client, token,
            Enumerable.Repeat($"/api/products/search?q={coldTerm}", options.LoadRequests).ToList(),
            options.LoadConcurrency, counter);
        results.Add(warm);
        Report(warm);

        counter.Reset();
        var mixed = await DriveAsync(label, Phase.MixedCorpus, client, token,
            MixedRequests(options.LoadRequests), options.LoadConcurrency, counter);
        results.Add(mixed);
        Report(mixed);

        return results;
    }

    /// <summary>
    /// A spread over the endpoints, deliberately including several pages of one price-filtered query:
    /// that is the case where the candidate set is cached independently of pagination, so the whole
    /// spread should cost a small, bounded number of upstream calls rather than one per page.
    /// </summary>
    private static List<string> MixedRequests(int count)
    {
        string[] shapes =
        [
            "/api/products?page=0&size=20",
            "/api/products?page=1&size=20",
            "/api/products/1",
            "/api/products/42",
            "/api/products/categories",
            "/api/products/search?q=phone",
            "/api/products/filter?category=beauty",
            "/api/products/filter?minPrice=10&maxPrice=100&page=0&size=5",
            "/api/products/filter?minPrice=10&maxPrice=100&page=1&size=5",
            "/api/products/filter?minPrice=10&maxPrice=100&page=2&size=5"
        ];

        return [.. Enumerable.Range(0, count).Select(i => shapes[i % shapes.Length])];
    }

    private static async Task<PhaseResult> DriveAsync(
        string label, Phase phase, HttpClient client, string token,
        IReadOnlyList<string> urls, int concurrency, UpstreamCounter counter)
    {
        var latencies = new double[urls.Count];
        var failures = 0;
        var next = -1;

        var wall = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= urls.Count)
                {
                    return;
                }

                var request = new HttpRequestMessage(HttpMethod.Get, urls[index]);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    using var response = await client.SendAsync(request);
                    await response.Content.ReadAsByteArrayAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    Interlocked.Increment(ref failures);
                }
                finally
                {
                    request.Dispose();
                    latencies[index] = stopwatch.Elapsed.TotalMilliseconds;
                }
            }
        });

        await Task.WhenAll(workers);
        wall.Stop();

        // The proxy increments before forwarding, but a call in flight when the last response lands
        // would still be counted late; a short settle keeps the number honest.
        await Task.Delay(250);

        return new PhaseResult(label, phase, urls.Count, counter.Calls, wall.Elapsed.TotalMilliseconds,
            latencies, failures);
    }

    private static void Report(PhaseResult result) =>
        Console.WriteLine($"  {result.Service,-6} {result.Phase,-17} "
                          + $"upstream={result.UpstreamCalls,4}  "
                          + $"p50={result.Percentile(0.50),7:F1}ms  p95={result.Percentile(0.95),7:F1}ms  "
                          + $"rps={result.RequestsPerSecond,7:F0}  failures={result.NonSuccess}");

    private static async Task<string> LoginAsync(HttpClient client, HarnessOptions options)
    {
        var payload = JsonSerializer.Serialize(new { username = options.Username, password = options.Password });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/auth/login", content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"login answered {(int)response.StatusCode}: {body}");
        }

        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    // --- report -------------------------------------------------------------

    private static string Render(HarnessOptions options, List<PhaseResult> results)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# Load comparison");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Run: {DateTime.UtcNow:u}");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"- Concurrency: **{options.LoadConcurrency}**; requests per throughput phase: **{options.LoadRequests}**");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"- Both services forward through a counting proxy to `{options.UpstreamOrigin}`, so upstream");
        builder.AppendLine("  calls are counted, and the extra hop is common to both.");
        builder.AppendLine();

        foreach (var phase in Enum.GetValues<Phase>())
        {
            var rows = results.Where(r => r.Phase == phase).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            builder.AppendLine(CultureInfo.InvariantCulture, $"## {Describe(phase)}");
            builder.AppendLine();
            builder.AppendLine("| Service | Requests | Upstream calls | p50 | p95 | p99 | max | req/s | Failures |");
            builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var row in rows)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"| {row.Service} | {row.Requests} | **{row.UpstreamCalls}** "
                    + $"| {row.Percentile(0.50):F1} ms | {row.Percentile(0.95):F1} ms | {row.Percentile(0.99):F1} ms "
                    + $"| {row.Latencies.DefaultIfEmpty(0).Max():F1} ms | {row.RequestsPerSecond:F0} | {row.NonSuccess} |");
            }
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string Describe(Phase phase) => phase switch
    {
        Phase.ColdSingleFlight =>
            "Cold single-flight — every worker requests the same uncached query at once; "
            + "stampede protection means exactly one upstream call",
        Phase.WarmThroughput =>
            "Warm throughput — the same query, now cached; latency is the service's own overhead and "
            + "the upstream count must stay at zero",
        _ => "Mixed corpus — a spread of endpoints, including several pages of one price-filtered query"
    };
}
