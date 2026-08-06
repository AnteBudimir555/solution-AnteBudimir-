using System.Globalization;
using Middleware.Api.Errors;

namespace Middleware.Api.Validation;

/// <summary>
/// Explicit conversion of raw query/route values to their target types.
///
/// <para>Endpoint handlers take these parameters as <c>string?</c> and convert here, rather than
/// letting minimal-API model binding do it, because a binding failure would produce an empty-bodied
/// 400. Spring reports the same failure as a <c>MethodArgumentTypeMismatchException</c> rendered as a
/// ProblemDetail naming the offending parameter, and this reproduces that contract exactly.</para>
///
/// <para>An absent value and a present-but-empty value are treated alike (the parameter default
/// applies), matching Spring's handling of an omitted request parameter.</para>
/// </summary>
internal static class QueryParsing
{
    public static int Int(string? raw, string name, int fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ParameterTypeMismatchException(name);
    }

    public static long Long(string? raw, string name)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ParameterTypeMismatchException(name);
        }
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ParameterTypeMismatchException(name);
    }

    public static decimal? Decimal(string? raw, string name)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ParameterTypeMismatchException(name);
    }
}
