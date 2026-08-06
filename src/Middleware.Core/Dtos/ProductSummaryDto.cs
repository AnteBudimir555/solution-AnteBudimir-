namespace Middleware.Core.Dtos;

/// <summary>
/// Trimmed product shape used by the list/filter/search endpoints: just the image, name, price and
/// a short description capped at 100 characters.
/// </summary>
public sealed record ProductSummaryDto(
    string? Image,
    string? Name,
    decimal? Price,
    string? ShortDescription);
