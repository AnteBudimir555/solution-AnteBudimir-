namespace Middleware.Core.Exceptions;

/// <summary>Thrown when a requested product does not exist in the source. Maps to HTTP 404.</summary>
public sealed class ProductNotFoundException(long id)
    : Exception($"Product not found: {id}");
