package com.abysalto.middleware.service;

import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.dto.ProductDetailDto;
import com.abysalto.middleware.dto.ProductSummaryDto;
import com.abysalto.middleware.util.TextUtils;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Component;

/**
 * Maps internal {@link Product} domain objects to the API DTOs, applying the short-description
 * truncation for summaries.
 */
@Component
public class ProductMapper {

    private final int descriptionMaxLength;

    public ProductMapper(@Value("${middleware.summary.description-max-length:100}") int descriptionMaxLength) {
        this.descriptionMaxLength = descriptionMaxLength;
    }

    public ProductSummaryDto toSummary(Product p) {
        return new ProductSummaryDto(
                p.thumbnail(),
                p.title(),
                p.price(),
                TextUtils.truncate(p.description(), descriptionMaxLength)
        );
    }

    public ProductDetailDto toDetail(Product p) {
        return new ProductDetailDto(
                p.id(),
                p.title(),
                p.description(),
                p.category(),
                p.price(),
                p.discountPercentage(),
                p.rating(),
                p.stock(),
                p.tags(),
                p.brand(),
                p.sku(),
                p.weight(),
                p.dimensions(),
                p.warrantyInformation(),
                p.shippingInformation(),
                p.availabilityStatus(),
                p.returnPolicy(),
                p.minimumOrderQuantity(),
                p.thumbnail(),
                p.images(),
                p.reviews(),
                p.meta()
        );
    }
}
