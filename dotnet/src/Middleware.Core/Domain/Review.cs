using System.ComponentModel;

namespace Middleware.Core.Domain;

/// <summary>
/// A single customer review for a product. The reviewer's email is intentionally not carried here:
/// it is upstream PII and must not be re-exposed through the detail endpoint.
/// <para>Reachable from a cached <see cref="Product"/>, so it carries the same immutability contract.</para>
/// </summary>
[ImmutableObject(true)]
public sealed record Review(
    int? Rating,
    string? Comment,
    string? Date,
    string? ReviewerName);
