using System.ComponentModel;

namespace Middleware.Core.Domain;

/// <summary>
/// Auxiliary metadata for a product (timestamps, barcode, etc.).
/// <para>Reachable from a cached <see cref="Product"/>, so it carries the same immutability contract.</para>
/// </summary>
[ImmutableObject(true)]
public sealed record Meta(
    string? CreatedAt,
    string? UpdatedAt,
    string? Barcode,
    string? QrCode);
