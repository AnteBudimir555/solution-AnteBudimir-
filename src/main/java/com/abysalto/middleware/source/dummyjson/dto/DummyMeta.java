package com.abysalto.middleware.source.dummyjson.dto;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;

@JsonIgnoreProperties(ignoreUnknown = true)
public record DummyMeta(
        String createdAt,
        String updatedAt,
        String barcode,
        String qrCode
) {
}
