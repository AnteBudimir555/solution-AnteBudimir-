using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Middleware.Api.Serialization;

/// <summary>
/// Writes <see cref="double"/> the way Jackson does, so the JSON the middleware emits is byte-identical
/// to the Java service's.
///
/// <para>Jackson renders a <c>Double</c> via <c>Double.toString</c>, which always includes a decimal
/// point: a weight of 4 goes out as <c>4.0</c>. <c>System.Text.Json</c> writes the shortest round-trip
/// form, <c>4</c>. Both parse back to the same value, but they are different bytes on the wire, and the
/// shadowing harness reports them as a diff — correctly, since a client comparing raw payloads would
/// see it too. Only whole values need fixing; every other value already round-trips to the same text.</para>
///
/// <para>Applies to <see cref="double"/> only. Prices are <see cref="decimal"/> (Java
/// <c>BigDecimal</c>) and keep their own scale, and integral fields are <c>int</c>/<c>long</c>.</para>
/// </summary>
internal sealed class JavaDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);

        // A representation with no '.', 'E' or 'N'/'I' (NaN/Infinity) is a whole number that Java would
        // have written with a trailing ".0".
        if (text.AsSpan().IndexOfAny('.', 'E', 'e') < 0 && double.IsFinite(value))
        {
            writer.WriteRawValue(text + ".0", skipInputValidation: true);
            return;
        }

        writer.WriteNumberValue(value);
    }
}
