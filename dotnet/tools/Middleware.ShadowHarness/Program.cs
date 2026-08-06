using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Middleware.ShadowHarness;

/// <summary>
/// API-shadowing harness: replays <see cref="Corpus"/> against the Java and .NET services — both
/// pointed at the same live DummyJSON — and diffs status, content type and normalized JSON body.
/// For a middleware whose whole job is re-shaping an upstream API, this is the most direct parity
/// proof available, and the one the migration plan gates cutover on.
///
/// <para>Usage:
/// <c>dotnet run -- --java http://localhost:8080 --dotnet http://localhost:5156 --out report.md</c>.
/// Exits non-zero when any case diverges, so it can gate a pipeline.</para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = HarnessOptions.Parse(args);
        Console.WriteLine($"Shadowing {options.JavaBaseUrl} (java) against {options.DotnetBaseUrl} (dotnet)");

        using var java = CreateClient(options.JavaBaseUrl);
        using var dotnet = CreateClient(options.DotnetBaseUrl);

        if (await Unreachable(java, "java") || await Unreachable(dotnet, "dotnet"))
        {
            return 2;
        }

        var javaToken = await LoginAsync(java, options, "java");
        var dotnetToken = await LoginAsync(dotnet, options, "dotnet");
        var expiredToken = MintExpiredToken(options);

        var cases = Corpus.All();
        var known = Corpus.KnownDivergences().ToDictionary(d => d.CaseName, StringComparer.Ordinal);
        var results = new List<CaseResult>(cases.Count);

        foreach (var testCase in cases)
        {
            // Sequential and back-to-back per case: both services see the same upstream state, and the
            // 60-second cache TTL cannot expire between the two halves of a comparison.
            var javaResponse = await SendAsync(java, testCase, javaToken, expiredToken);
            var dotnetResponse = await SendAsync(dotnet, testCase, dotnetToken, expiredToken);

            known.TryGetValue(testCase.Name, out var divergence);
            var result = CaseResult.Compare(testCase, javaResponse, dotnetResponse, divergence);
            results.Add(result);
            Console.WriteLine($"  {result.Label} {testCase.Name}");
        }

        var report = Report.Render(options, results);
        await File.WriteAllTextAsync(options.OutputPath, report);

        var identical = results.Count(r => r.Matches);
        var accepted = results.Count(r => !r.Matches && r.IsKnown);
        var unexpected = results.Count(r => !r.Matches && !r.IsKnown);

        Console.WriteLine();
        Console.WriteLine($"{identical}/{results.Count} cases identical; "
                          + $"{accepted} known divergence(s); {unexpected} unexpected.");
        Console.WriteLine($"Report written to {options.OutputPath}");
        return unexpected == 0 ? 0 : 1;
    }

    private static HttpClient CreateClient(string baseUrl) =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };

    private static async Task<bool> Unreachable(HttpClient client, string label)
    {
        try
        {
            // Any response at all proves the service is listening; the status is irrelevant here.
            await client.GetAsync("/api/products/categories");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"The {label} service at {client.BaseAddress} is not reachable: {ex.Message}");
            return true;
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, HarnessOptions options, string label)
    {
        var payload = JsonSerializer.Serialize(new { username = options.Username, password = options.Password });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/auth/login", content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not authenticate against the {label} service ({(int)response.StatusCode}): {body}");
        }

        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>
    /// A well-formed HS256 token that expired an hour ago, signed with the shared dev secret and
    /// carrying the expected issuer — so only the expiry can be the reason either service rejects it.
    /// </summary>
    private static string MintExpiredToken(HarnessOptions options)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSecret));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.JwtIssuer,
            Subject = new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(
                JwtRegisteredClaimNames.Sub, options.Username)]),
            IssuedAt = DateTime.UtcNow.AddHours(-2),
            NotBefore = DateTime.UtcNow.AddHours(-2),
            Expires = DateTime.UtcNow.AddHours(-1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static async Task<ServiceResponse> SendAsync(
        HttpClient client, ShadowCase testCase, string validToken, string expiredToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(testCase.Method), testCase.PathAndQuery);

        var token = testCase.Auth switch
        {
            Auth.Valid => validToken,
            Auth.Malformed => "not-a-real-token",
            Auth.Expired => expiredToken,
            _ => null
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (testCase.Body is not null)
        {
            request.Content = new StringContent(testCase.Body, Encoding.UTF8, "application/json");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();
            return new ServiceResponse(
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? "<none>",
                JsonNormalizer.Canonicalize(body),
                stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            stopwatch.Stop();
            // A transport failure is itself a comparable outcome: if only one side fails, that is the diff.
            return new ServiceResponse(0, "<transport-error>", ex.Message, stopwatch.Elapsed.TotalMilliseconds);
        }
    }
}

/// <summary>What one service answered, already canonicalized for comparison.</summary>
internal sealed record ServiceResponse(int Status, string ContentType, string Body, double ElapsedMs);

/// <summary>The outcome of one replayed case across both services.</summary>
internal sealed record CaseResult(
    ShadowCase Case,
    ServiceResponse Java,
    ServiceResponse Dotnet,
    bool StatusMatches,
    bool ContentTypeMatches,
    bool BodyMatches,
    KnownDivergence? Known)
{
    public bool Matches => StatusMatches && ContentTypeMatches && BodyMatches;

    public bool IsKnown => Known is not null;

    /// <summary>Console marker: a known divergence is reported, but does not fail the run.</summary>
    public string Label => Matches ? "ok  " : IsKnown ? "note" : "DIFF";

    public static CaseResult Compare(
        ShadowCase testCase, ServiceResponse java, ServiceResponse dotnet, KnownDivergence? known) =>
        new(testCase,
            java,
            dotnet,
            java.Status == dotnet.Status,
            string.Equals(java.ContentType, dotnet.ContentType, StringComparison.OrdinalIgnoreCase),
            string.Equals(java.Body, dotnet.Body, StringComparison.Ordinal),
            known);
}

/// <summary>Command-line configuration, with defaults matching the two services' local dev profiles.</summary>
internal sealed record HarnessOptions(
    string JavaBaseUrl,
    string DotnetBaseUrl,
    string Username,
    string Password,
    string JwtSecret,
    string JwtIssuer,
    string OutputPath)
{
    public static HarnessOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            values[args[i].TrimStart('-')] = args[i + 1];
        }

        string Value(string key, string fallback) => values.TryGetValue(key, out var v) ? v : fallback;

        return new HarnessOptions(
            Value("java", "http://localhost:8080"),
            Value("dotnet", "http://localhost:5156"),
            Value("username", "demo"),
            Value("password", "demo1234"),
            // Both dev profiles ship this same value, which is what lets one minted token be replayed
            // against both services.
            Value("secret", "dev-only-insecure-jwt-secret-please-override-in-real-environments-0123456789"),
            Value("issuer", "abysalto-middleware"),
            Value("out", "shadow-report.md"));
    }
}

/// <summary>Renders the run as a markdown report: a summary, then one section per diverging case.</summary>
internal static class Report
{
    public static string Render(HarnessOptions options, IReadOnlyList<CaseResult> results)
    {
        var builder = new StringBuilder();
        var diffs = results.Where(r => !r.Matches).ToList();
        var unexpected = diffs.Count(r => !r.IsKnown);

        builder.AppendLine("# API shadowing report");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Run: {DateTime.UtcNow:u}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Java service: `{options.JavaBaseUrl}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- .NET service: `{options.DotnetBaseUrl}`");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"- Cases: **{results.Count}** — identical **{results.Count - diffs.Count}**, "
            + $"known divergences **{diffs.Count - unexpected}**, unexpected **{unexpected}**");
        builder.AppendLine();
        builder.AppendLine("`token` and `timestamp` are masked before comparison: the two services mint their own.");
        builder.AppendLine("Everything else — including number formatting — is compared as written on the wire.");
        builder.AppendLine();

        builder.AppendLine("## Per group");
        builder.AppendLine();
        builder.AppendLine("| Group | Cases | Identical | Known | Unexpected |");
        builder.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var group in results.GroupBy(r => r.Case.Group))
        {
            var identical = group.Count(r => r.Matches);
            var accepted = group.Count(r => !r.Matches && r.IsKnown);
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key} | {group.Count()} | {identical} | {accepted} "
                + $"| {group.Count() - identical - accepted} |");
        }
        builder.AppendLine();

        if (diffs.Count == 0)
        {
            builder.AppendLine("## Diffs");
            builder.AppendLine();
            builder.AppendLine("None — every case matched on status, content type and normalized body.");
            return builder.ToString();
        }

        builder.AppendLine("## Diffs");
        builder.AppendLine();
        foreach (var diff in diffs)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"### `{diff.Case.Name}`{(diff.IsKnown ? " — known divergence" : string.Empty)}");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"`{diff.Case.Method} {diff.Case.PathAndQuery}` (auth: {diff.Case.Auth})");
            builder.AppendLine();
            if (diff.Known is { } known)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"> {known.Reason}");
                builder.AppendLine();
            }
            builder.AppendLine("| | java | dotnet |");
            builder.AppendLine("|---|---|---|");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| status | {diff.Java.Status} | {diff.Dotnet.Status} |{Flag(diff.StatusMatches)}");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| content-type | `{diff.Java.ContentType}` | `{diff.Dotnet.ContentType}` |{Flag(diff.ContentTypeMatches)}");
            builder.AppendLine();

            if (!diff.BodyMatches)
            {
                builder.AppendLine("First body difference:");
                builder.AppendLine();
                builder.AppendLine("```");
                builder.AppendLine(JsonNormalizer.FirstDifference(diff.Java.Body, diff.Dotnet.Body));
                builder.AppendLine("```");
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string Flag(bool matches) => matches ? string.Empty : " **≠**";
}
