using Microsoft.Extensions.Options;
using Middleware.Core.Common;
using Middleware.Core.Domain;
using Middleware.Core.Dtos;
using Middleware.Core.Options;

namespace Middleware.Core.Services;

/// <summary>
/// Maps internal <see cref="Product"/> domain objects to the API DTOs, applying the short-description
/// truncation for summaries.
/// </summary>
public sealed class ProductMapper
{
    private readonly int _descriptionMaxLength;

    public ProductMapper(IOptions<SummaryOptions> options)
    {
        _descriptionMaxLength = options.Value.DescriptionMaxLength;
    }

    public ProductSummaryDto ToSummary(Product p) =>
        new(
            p.Thumbnail,
            p.Title,
            p.Price,
            TextUtils.Truncate(p.Description, _descriptionMaxLength));

    public ProductDetailDto ToDetail(Product p) =>
        new(
            p.Id,
            p.Title,
            p.Description,
            p.Category,
            p.Price,
            p.DiscountPercentage,
            p.Rating,
            p.Stock,
            p.Tags,
            p.Brand,
            p.Sku,
            p.Weight,
            p.Dimensions,
            p.WarrantyInformation,
            p.ShippingInformation,
            p.AvailabilityStatus,
            p.ReturnPolicy,
            p.MinimumOrderQuantity,
            p.Thumbnail,
            p.Images,
            p.Reviews,
            p.Meta);
}
