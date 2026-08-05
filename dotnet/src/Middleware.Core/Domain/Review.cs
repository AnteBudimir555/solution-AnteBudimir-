namespace Middleware.Core.Domain;

/// <summary>
/// A single customer review for a product. The reviewer's email is intentionally not carried here:
/// it is upstream PII and must not be re-exposed through the detail endpoint.
/// </summary>
public sealed record Review(
    int? Rating,
    string? Comment,
    string? Date,
    string? ReviewerName);
