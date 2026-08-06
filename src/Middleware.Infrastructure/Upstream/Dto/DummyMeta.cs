namespace Middleware.Infrastructure.Upstream.Dto;

/// <summary>Raw DummyJSON product metadata (timestamps, barcode, QR code).</summary>
internal sealed record DummyMeta(
    string? CreatedAt,
    string? UpdatedAt,
    string? Barcode,
    string? QrCode);
