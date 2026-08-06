using System.Text;
using System.Text.Json;

namespace Middleware.ShadowHarness;

/// <summary>
/// Renders a response body in a canonical form so two services can be compared on content rather than
/// on incidental serialization choices.
///
/// <para>Object members are emitted in sorted order (member order is not part of the JSON contract),
/// arrays keep their order (it <em>is</em> part of the contract), and numbers keep their raw text —
/// a service emitting <c>10.0</c> where the other emits <c>10.00</c> is a real client-visible
/// difference and must surface as a diff, not be normalized away.</para>
///
/// <para>Only values that <em>cannot</em> match by construction are masked: the two services mint
/// their own tokens and stamp their own error timestamps.</para>
/// </summary>
internal static class JsonNormalizer
{
    /// <summary>Property names whose values differ per response by construction, never by contract.</summary>
    private static readonly HashSet<string> VolatileMembers =
        new(StringComparer.Ordinal) { "timestamp", "token" };

    private const string Mask = "<masked>";

    /// <summary>
    /// Canonicalizes a body. Non-JSON payloads (an empty body, or HTML from a framework error page)
    /// are returned trimmed and verbatim so they still compare — and still read — sensibly.
    /// </summary>
    public static string Canonicalize(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<empty>";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var builder = new StringBuilder();
            Write(document.RootElement, builder, 0);
            return builder.ToString();
        }
        catch (JsonException)
        {
            return body.Trim();
        }
    }

    private static void Write(JsonElement element, StringBuilder builder, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, builder, depth);
                break;

            case JsonValueKind.Array:
                WriteArray(element, builder, depth);
                break;

            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;

            default:
                // Numbers, booleans and null keep their exact on-the-wire text.
                builder.Append(element.GetRawText());
                break;
        }
    }

    private static void WriteObject(JsonElement element, StringBuilder builder, int depth)
    {
        var members = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        if (members.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        builder.Append("{\n");
        for (var i = 0; i < members.Count; i++)
        {
            Indent(builder, depth + 1);
            builder.Append(JsonSerializer.Serialize(members[i].Name)).Append(": ");

            if (VolatileMembers.Contains(members[i].Name))
            {
                builder.Append('"').Append(Mask).Append('"');
            }
            else
            {
                Write(members[i].Value, builder, depth + 1);
            }

            builder.Append(i < members.Count - 1 ? ",\n" : "\n");
        }
        Indent(builder, depth);
        builder.Append('}');
    }

    private static void WriteArray(JsonElement element, StringBuilder builder, int depth)
    {
        var items = element.EnumerateArray().ToList();
        if (items.Count == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append("[\n");
        for (var i = 0; i < items.Count; i++)
        {
            Indent(builder, depth + 1);
            Write(items[i], builder, depth + 1);
            builder.Append(i < items.Count - 1 ? ",\n" : "\n");
        }
        Indent(builder, depth);
        builder.Append(']');
    }

    private static void Indent(StringBuilder builder, int depth) => builder.Append(' ', depth * 2);

    /// <summary>
    /// The first differing line of two canonical bodies, as a compact "expected / actual" excerpt for
    /// the report. Whole-body dumps are unreadable for a 20-item page; the first divergence is what
    /// identifies the defect.
    /// </summary>
    public static string FirstDifference(string java, string dotnet)
    {
        var left = java.Split('\n');
        var right = dotnet.Split('\n');

        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var a = i < left.Length ? left[i] : "<end of body>";
            var b = i < right.Length ? right[i] : "<end of body>";
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                return $"line {i + 1}\n      java  : {a.Trim()}\n      dotnet: {b.Trim()}";
            }
        }

        return "<no line-level difference>";
    }
}
