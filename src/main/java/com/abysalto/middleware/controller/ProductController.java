package com.abysalto.middleware.controller;

import com.abysalto.middleware.dto.PagedResponse;
import com.abysalto.middleware.dto.ProductDetailDto;
import com.abysalto.middleware.dto.ProductSummaryDto;
import com.abysalto.middleware.exception.InvalidRequestException;
import com.abysalto.middleware.service.ProductService;
import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;
import jakarta.validation.constraints.Max;
import jakarta.validation.constraints.Min;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.PositiveOrZero;
import org.springframework.validation.annotation.Validated;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import java.math.BigDecimal;
import java.util.List;

/**
 * Product endpoints. All list/filter/search responses use the trimmed {@link ProductSummaryDto}
 * shape; only the single-product endpoint returns the full {@link ProductDetailDto}.
 */
@RestController
@RequestMapping("/api/products")
@Validated
@Tag(name = "Products", description = "Browse, filter and search products")
public class ProductController {

    /**
     * Upper bound on the requested page index. With the max page size of 100 this bounds
     * {@code page * size} well within {@code int} range, so the offset passed downstream can never
     * overflow into a negative value.
     */
    private static final long MAX_PAGE = 10_000;

    private final ProductService service;

    public ProductController(ProductService service) {
        this.service = service;
    }

    @GetMapping
    @Operation(summary = "List products", description = "Paginated trimmed product list")
    public PagedResponse<ProductSummaryDto> list(
            @RequestParam(defaultValue = "0") @Min(0) @Max(MAX_PAGE) int page,
            @RequestParam(defaultValue = "20") @Min(1) @Max(100) int size) {
        return service.list(page, size);
    }

    @GetMapping("/{id}")
    @Operation(summary = "Product details", description = "Full detail of a single product by id")
    public ProductDetailDto getById(@PathVariable long id) {
        return service.getById(id);
    }

    @GetMapping("/filter")
    @Operation(summary = "Filter products", description = "Filter by category and/or price range (combinable)")
    public PagedResponse<ProductSummaryDto> filter(
            @RequestParam(required = false) String category,
            @RequestParam(required = false) @PositiveOrZero BigDecimal minPrice,
            @RequestParam(required = false) @PositiveOrZero BigDecimal maxPrice,
            @RequestParam(defaultValue = "0") @Min(0) @Max(MAX_PAGE) int page,
            @RequestParam(defaultValue = "20") @Min(1) @Max(100) int size) {
        if (minPrice != null && maxPrice != null && minPrice.compareTo(maxPrice) > 0) {
            throw new InvalidRequestException("minPrice must not be greater than maxPrice");
        }
        return service.filter(category, minPrice, maxPrice, page, size);
    }

    @GetMapping("/search")
    @Operation(summary = "Search products by name", description = "Free-text search over product names")
    public PagedResponse<ProductSummaryDto> search(
            @RequestParam @NotBlank String q,
            @RequestParam(defaultValue = "0") @Min(0) @Max(MAX_PAGE) int page,
            @RequestParam(defaultValue = "20") @Min(1) @Max(100) int size) {
        return service.searchByName(q, page, size);
    }

    @GetMapping("/categories")
    @Operation(summary = "List categories", description = "Available product category identifiers")
    public List<String> categories() {
        return service.categories();
    }
}
