namespace Middleware.Infrastructure.Upstream.Dto;

/// <summary>
/// Raw DummyJSON review payload. <see cref="ReviewerEmail"/> is upstream PII and is deliberately
/// dropped by the mapper — it is never carried into the domain <c>Review</c> or surfaced by the API.
/// </summary>
internal sealed record DummyReview(
    int? Rating,
    string? Comment,
    string? Date,
    string? ReviewerName,
    string? ReviewerEmail);
