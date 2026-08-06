namespace Middleware.Api.Errors;

/// <summary>
/// Request validation failed. <see cref="Exception.Message"/> already carries the client-safe,
/// joined field/constraint detail and is rendered verbatim as the problem <c>detail</c>.
/// </summary>
public sealed class RequestValidationException(string detail) : Exception(detail);

/// <summary>
/// A query or route parameter could not be converted to its target type. Only the parameter name is
/// reported: the raw conversion message would name internal types.
/// </summary>
public sealed class ParameterTypeMismatchException(string parameterName)
    : Exception($"Parameter '{parameterName}' has an invalid value.")
{
    public string ParameterName { get; } = parameterName;
}

/// <summary>
/// A required query parameter was not supplied at all (as opposed to supplied blank, which is a
/// validation failure). Mirrors Spring's <c>MissingServletRequestParameterException</c> contract.
/// </summary>
public sealed class MissingParameterException(string parameterName)
    : Exception($"Required parameter '{parameterName}' is not present.");

/// <summary>
/// Login credentials were rejected. The cause (unknown user vs. wrong password) is never carried on
/// the exception: it must not reach the client, as it enables account enumeration.
/// </summary>
public sealed class InvalidCredentialsException(string reason) : Exception(reason);
