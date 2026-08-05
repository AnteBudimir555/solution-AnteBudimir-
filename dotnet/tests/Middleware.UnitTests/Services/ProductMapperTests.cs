using Microsoft.Extensions.Options;
using Middleware.Core.Domain;
using Middleware.Core.Options;
using Middleware.Core.Services;
using Middleware.UnitTests.Support;

namespace Middleware.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ProductMapper"/>: the summary shape carries only the trimmed fields (with
/// the description truncated to the configured cap), while the detail shape is a faithful full copy.
/// </summary>
public class ProductMapperTests
{
    private readonly ProductMapper _mapper =
        new(Options.Create(new SummaryOptions { DescriptionMaxLength = 100 }));

    [Fact]
    public void ToSummaryExposesOnlyTrimmedFields()
    {
        var product = TestData.Product(1, "Phone", "A short description", 9.99m, "smartphones");

        var summary = _mapper.ToSummary(product);

        Assert.Equal(product.Thumbnail, summary.Image);
        Assert.Equal("Phone", summary.Name);
        Assert.Equal(9.99m, summary.Price);
        Assert.Equal("A short description", summary.ShortDescription);
    }

    [Fact]
    public void ToSummaryTruncatesLongDescription()
    {
        var longDescription = string.Concat(Enumerable.Repeat("word ", 60)).Trim(); // ~300 chars
        var product = TestData.Product(1, "Phone", longDescription, 9.99m, "smartphones");

        var summary = _mapper.ToSummary(product);

        Assert.NotNull(summary.ShortDescription);
        Assert.True(summary.ShortDescription!.Length <= 100);
        Assert.EndsWith("…", summary.ShortDescription);
    }

    [Fact]
    public void ToSummaryKeepsNullDescriptionNull()
    {
        var product = TestData.Product(1, "Phone", null, 9.99m, "smartphones");
        Assert.Null(_mapper.ToSummary(product).ShortDescription);
    }

    [Fact]
    public void ToDetailCopiesAllFields()
    {
        var product = TestData.Product(42, "Laptop", "Full detail description", 1200.00m, "laptops");

        var detail = _mapper.ToDetail(product);

        Assert.Equal(42, detail.Id);
        Assert.Equal("Laptop", detail.Name);
        Assert.Equal(product.Thumbnail, detail.Image);
        Assert.Equal("Full detail description", detail.Description);
        Assert.Equal("laptops", detail.Category);
        Assert.Equal(1200.00m, detail.Price);
        Assert.Equal(product.Tags, detail.Tags);
        Assert.Equal(product.Reviews, detail.Reviews);
        Assert.Equal(product.Dimensions, detail.Dimensions);
        Assert.Equal(product.Meta, detail.Meta);
    }
}
