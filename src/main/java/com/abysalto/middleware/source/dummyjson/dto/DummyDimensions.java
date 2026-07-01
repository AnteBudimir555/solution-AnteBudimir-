package com.abysalto.middleware.source.dummyjson.dto;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;

@JsonIgnoreProperties(ignoreUnknown = true)
public record DummyDimensions(Double width, Double height, Double depth) {
}
