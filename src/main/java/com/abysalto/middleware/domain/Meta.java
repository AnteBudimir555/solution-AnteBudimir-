package com.abysalto.middleware.domain;

/** Auxiliary metadata for a product (timestamps, barcode, etc.). */
public record Meta(
        String createdAt,
        String updatedAt,
        String barcode,
        String qrCode
) {
}
