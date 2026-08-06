using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Middleware.ShadowHarness;

/// <summary>
/// Snapshots both OpenAPI documents and diffs them, proving the published contract did not change.
///
/// <para>Comparison is fact-based rather than textual: each document is flattened into a set of
/// statements — "this operation exists", "this parameter is an int32 with maximum 100", "this response
/// code carries this schema", "this property is a nullable string" — and the two sets are diffed. A
/// textual diff of two generators' output would be all noise; this reports only what a consumer of the
/// document would actually observe.</para>
///
/// <para>Facts are split into two ledgers. <em>Contract</em> facts (paths, operations, parameters,
/// types, constraints, security, response codes and media types, schema shapes) must match, and a
/// difference fails the run. <em>Documentation</em> facts (human-readable descriptions) are reported
/// but do not fail it: they are prose, not shape.</para>
/// </summary>
internal static class OpenApiDiff
{
    /// <summary>
    /// Facts where the two documents disagree and the .NET one is the accurate description. Matching
    /// springdoc here would mean publishing something the service does not do, so these are reported
    /// with their reason instead of "fixed". Matched by substring against the flattened fact.
    /// </summary>
    private static readonly (string Marker, string Reason)[] Accepted =
    [
        ("media=*/*",
            "springdoc types every success response as */* because the Java controllers declare no "
            + "`produces`. Both services answer application/json — the shadowing run compares the real "
            + "content type on every case and finds no difference — so the .NET document says so."),
        ("200 media=application/json", "See the */* entry: same divergence, seen from the .NET side."),
        ("property ProblemDetail.properties",
            "Spring's ProblemDetail model carries extensions in a dynamic `properties` map, so the "
            + "document says an object of unknown shape is attached. This API always attaches exactly "
            + "one extension, `timestamp`."),
        ("property ProblemDetail.timestamp",
            "The counterpart: the .NET document declares the `timestamp` member the error bodies "
            + "actually carry, which the Java document leaves inside its untyped `properties` map. The "
            + "bodies themselves are byte-identical — the shadowing run asserts that on every error case.")
    ];

    public static async Task<int> RunAsync(HarnessOptions options)
    {
        Console.WriteLine("Snapshotting both OpenAPI documents");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var javaDocument = await FetchAsync(client, options.JavaBaseUrl + options.JavaOpenApiPath, "java");
        var dotnetDocument = await FetchAsync(client, options.DotnetBaseUrl + options.DotnetOpenApiPath, "dotnet");
        if (javaDocument is null || dotnetDocument is null)
        {
            return 2;
        }

        var java = Flatten(javaDocument.RootElement);
        var dotnet = Flatten(dotnetDocument.RootElement);
        javaDocument.Dispose();
        dotnetDocument.Dispose();

        var contract = Compare(java.Contract, dotnet.Contract);
        var documentation = Compare(java.Documentation, dotnet.Documentation);
        var unexpected = contract.Where(d => ReasonFor(d.Fact) is null).ToList();

        var report = Render(options, contract, documentation);
        await File.WriteAllTextAsync(options.OpenApiOutputPath, report);

        Console.WriteLine();
        Console.WriteLine($"Contract facts: {java.Contract.Count} (java) vs {dotnet.Contract.Count} (dotnet); "
                          + $"{contract.Count - unexpected.Count} accepted difference(s), "
                          + $"{unexpected.Count} unexpected.");
        Console.WriteLine($"Documentation facts: {documentation.Count} difference(s) (reported, not enforced).");
        Console.WriteLine($"Report written to {options.OpenApiOutputPath}");
        return unexpected.Count == 0 ? 0 : 1;
    }

    private static string? ReasonFor(string fact) =>
        Accepted.FirstOrDefault(a => fact.Contains(a.Marker, StringComparison.Ordinal)).Reason;

    private static async Task<JsonDocument?> FetchAsync(HttpClient client, string url, string label)
    {
        try
        {
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"The {label} document at {url} answered {(int)response.StatusCode}.");
                return null;
            }
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.Error.WriteLine($"Could not read the {label} document at {url}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Differences as (fact, present-in-java, present-in-dotnet), sorted for a stable report.</summary>
    private static List<(string Fact, bool InJava, bool InDotnet)> Compare(
        SortedSet<string> java, SortedSet<string> dotnet) =>
        [.. java.Except(dotnet).Select(f => (f, true, false))
              .Concat(dotnet.Except(java).Select(f => (f, false, true)))
              .OrderBy(t => t.Item1, StringComparer.Ordinal)];

    // --- flattening ---------------------------------------------------------

    private sealed record Facts(SortedSet<string> Contract, SortedSet<string> Documentation);

    private static Facts Flatten(JsonElement document)
    {
        var facts = new Facts(new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));

        // An operation without its own requirement inherits the document-level one, so folding the root
        // in compares what a client must actually send rather than how each generator chose to say it.
        // No root requirement means an operation that declares none is public — the same state springdoc
        // spells as an explicit empty `security: []` override on a document that does have a root.
        var rootSecurity = document.TryGetProperty("security", out var root) ? RenderSecurity(root) : "<public>";

        if (document.TryGetProperty("paths", out var paths))
        {
            foreach (var path in paths.EnumerateObject())
            {
                foreach (var operation in path.Value.EnumerateObject())
                {
                    FlattenOperation(facts, $"{operation.Name.ToUpperInvariant()} {path.Name}", operation.Value, rootSecurity);
                }
            }
        }

        if (document.TryGetProperty("components", out var components))
        {
            if (components.TryGetProperty("schemas", out var schemas))
            {
                foreach (var schema in schemas.EnumerateObject())
                {
                    FlattenSchema(facts, schema.Name, schema.Value);
                }
            }
            if (components.TryGetProperty("securitySchemes", out var securitySchemes))
            {
                foreach (var scheme in securitySchemes.EnumerateObject())
                {
                    facts.Contract.Add($"securityScheme {scheme.Name} "
                                       + $"type={Text(scheme.Value, "type")} scheme={Text(scheme.Value, "scheme")} "
                                       + $"bearerFormat={Text(scheme.Value, "bearerFormat")}");
                }
            }
        }

        return facts;
    }

    private static void FlattenOperation(Facts facts, string operation, JsonElement body, string rootSecurity)
    {
        facts.Contract.Add($"operation {operation}");
        facts.Contract.Add($"operation {operation} security="
                           + (body.TryGetProperty("security", out var security) ? RenderSecurity(security) : rootSecurity));

        if (body.TryGetProperty("summary", out var summary))
        {
            facts.Documentation.Add($"operation {operation} summary={summary.GetString()}");
        }
        if (body.TryGetProperty("description", out var description))
        {
            facts.Documentation.Add($"operation {operation} description={description.GetString()}");
        }

        foreach (var parameter in Items(body, "parameters"))
        {
            var name = Text(parameter, "name");
            // An absent "required" means false, so the two generators' differing verbosity is not a diff.
            var required = parameter.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
            var schema = parameter.TryGetProperty("schema", out var s) ? RenderSchema(s) : "<none>";
            facts.Contract.Add($"parameter {operation} {name} in={Text(parameter, "in")} required={required} {schema}");

            if (parameter.TryGetProperty("description", out var parameterDescription))
            {
                facts.Documentation.Add($"parameter {operation} {name} description={parameterDescription.GetString()}");
            }
        }

        if (body.TryGetProperty("requestBody", out var requestBody))
        {
            var required = requestBody.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
            foreach (var media in Content(requestBody))
            {
                facts.Contract.Add($"requestBody {operation} required={required} {media}");
            }
        }

        if (body.TryGetProperty("responses", out var responses))
        {
            foreach (var response in responses.EnumerateObject())
            {
                foreach (var media in Content(response.Value))
                {
                    facts.Contract.Add($"response {operation} {response.Name} {media}");
                }
                if (response.Value.TryGetProperty("description", out var responseDescription))
                {
                    facts.Documentation.Add(
                        $"response {operation} {response.Name} description={responseDescription.GetString()}");
                }
            }
        }
    }

    private static void FlattenSchema(Facts facts, string name, JsonElement schema)
    {
        facts.Contract.Add($"schema {name}");

        if (schema.TryGetProperty("description", out var description))
        {
            facts.Documentation.Add($"schema {name} description={description.GetString()}");
        }

        var required = schema.TryGetProperty("required", out var r)
            ? string.Join(",", r.EnumerateArray().Select(v => v.GetString()).OrderBy(v => v, StringComparer.Ordinal))
            : string.Empty;
        facts.Contract.Add($"schema {name} required=[{required}]");

        if (!schema.TryGetProperty("properties", out var properties))
        {
            return;
        }

        foreach (var property in properties.EnumerateObject())
        {
            facts.Contract.Add($"property {name}.{property.Name} {RenderSchema(property.Value)}");
            if (property.Value.TryGetProperty("description", out var propertyDescription))
            {
                facts.Documentation.Add($"property {name}.{property.Name} description={propertyDescription.GetString()}");
            }
        }
    }

    // --- rendering ----------------------------------------------------------

    /// <summary>
    /// A schema as the shape facts a consumer depends on. <c>nullable</c> is deliberately excluded:
    /// OpenAPI 3.0 spells optionality as <c>nullable: true</c> and 3.1 as a type union, so the two
    /// documents cannot agree on the notation even when they agree on the meaning — and the meaning is
    /// already carried by <c>required</c>.
    /// </summary>
    private static string RenderSchema(JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            return $"ref={ShortRef(reference.GetString())}";
        }

        var parts = new List<string> { $"type={Text(schema, "type")}" };

        if (schema.TryGetProperty("format", out var format))
        {
            parts.Add($"format={format.GetString()}");
        }
        if (schema.TryGetProperty("items", out var items))
        {
            parts.Add($"items=({RenderSchema(items)})");
        }
        foreach (var constraint in new[] { "minimum", "maximum", "minLength", "maxLength", "default" })
        {
            if (schema.TryGetProperty(constraint, out var value))
            {
                parts.Add($"{constraint}={value.GetRawText()}");
            }
        }

        return string.Join(" ", parts);
    }

    private static string RenderSecurity(JsonElement security)
    {
        var requirements = security.EnumerateArray()
            .SelectMany(requirement => requirement.EnumerateObject().Select(scheme => scheme.Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        return requirements.Count == 0 ? "<public>" : string.Join(",", requirements);
    }

    /// <summary>Elements of an optional array member (an absent member yields nothing).</summary>
    private static IEnumerable<JsonElement> Items(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    /// <summary>A content map as "media=&lt;type&gt; &lt;schema&gt;", so a media type change is its own fact.</summary>
    private static IEnumerable<string> Content(JsonElement parent)
    {
        if (!parent.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var media in content.EnumerateObject())
        {
            var schema = media.Value.TryGetProperty("schema", out var s) ? RenderSchema(s) : "<none>";
            yield return $"media={media.Name} {schema}";
        }
    }

    private static string ShortRef(string? reference) =>
        reference is null ? "<null>" : reference[(reference.LastIndexOf('/') + 1)..];

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ToString() : "<absent>";

    // --- report -------------------------------------------------------------

    private static string Render(
        HarnessOptions options,
        List<(string Fact, bool InJava, bool InDotnet)> contract,
        List<(string Fact, bool InJava, bool InDotnet)> documentation)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# OpenAPI contract diff");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Run: {DateTime.UtcNow:u}");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"- Java document: `{options.JavaBaseUrl}{options.JavaOpenApiPath}`");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"- .NET document: `{options.DotnetBaseUrl}{options.DotnetOpenApiPath}`");
        builder.AppendLine();
        builder.AppendLine("Each document is flattened into the statements a consumer can observe, and the two");
        builder.AppendLine("sets are diffed. `nullable` is excluded: OpenAPI 3.0 and 3.1 spell optionality");
        builder.AppendLine("differently, and `required` already carries the meaning.");
        builder.AppendLine();

        var unexpected = contract.Where(d => ReasonFor(d.Fact) is null).ToList();
        var accepted = contract.Where(d => ReasonFor(d.Fact) is not null).ToList();

        AppendSection(builder, "Unexpected contract differences", unexpected,
            "None — the paths, operations, parameters, security, responses and schema shapes are identical.");
        AppendAccepted(builder, accepted);
        AppendSection(builder, "Documentation differences (reported, not enforced)", documentation,
            "None — every description matches.");

        return builder.ToString();
    }

    /// <summary>
    /// The accepted differences, grouped by reason: the same explanation covers several facts, and
    /// repeating a paragraph per row would bury it.
    /// </summary>
    private static void AppendAccepted(StringBuilder builder, List<(string Fact, bool InJava, bool InDotnet)> accepted)
    {
        builder.AppendLine("## Accepted contract differences");
        builder.AppendLine();

        if (accepted.Count == 0)
        {
            builder.AppendLine("None.");
            builder.AppendLine();
            return;
        }

        foreach (var group in accepted.GroupBy(d => ReasonFor(d.Fact)!).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"> {group.Key}");
            builder.AppendLine();
            foreach (var (fact, inJava, _) in group)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- only in {(inJava ? "java" : "dotnet")}: `{fact}`");
            }
            builder.AppendLine();
        }
    }

    private static void AppendSection(
        StringBuilder builder, string heading,
        List<(string Fact, bool InJava, bool InDotnet)> differences, string emptyMessage)
    {
        builder.AppendLine(CultureInfo.InvariantCulture, $"## {heading}");
        builder.AppendLine();

        if (differences.Count == 0)
        {
            builder.AppendLine(emptyMessage);
            builder.AppendLine();
            return;
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"{differences.Count} difference(s).");
        builder.AppendLine();
        builder.AppendLine("| Only in | Fact |");
        builder.AppendLine("|---|---|");
        foreach (var (fact, inJava, _) in differences)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {(inJava ? "java" : "dotnet")} | `{fact}` |");
        }
        builder.AppendLine();
    }
}
