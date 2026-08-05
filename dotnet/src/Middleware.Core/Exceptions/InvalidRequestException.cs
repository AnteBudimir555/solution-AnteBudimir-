namespace Middleware.Core.Exceptions;

/// <summary>
/// Thrown for a semantically invalid request that field-level validation cannot express on its own
/// (e.g. <c>minPrice &gt; maxPrice</c>). Rendered as a 400. Using a dedicated type keeps the 400
/// mapping intentional: an unrelated argument fault deeper in the stack correctly surfaces as a 500.
/// </summary>
public sealed class InvalidRequestException(string message) : Exception(message);
