namespace Middleware.Core.Domain;

/// <summary>Auxiliary metadata for a product (timestamps, barcode, etc.).</summary>
public sealed record Meta(
    string? CreatedAt,
    string? UpdatedAt,
    string? Barcode,
    string? QrCode);
