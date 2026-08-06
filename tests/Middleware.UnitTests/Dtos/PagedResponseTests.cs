using Middleware.Core.Dtos;

namespace Middleware.UnitTests.Dtos;

/// <summary>
/// Port of the Java <c>PagedResponseTest</c>. Exercises the <see cref="PagedResponse{T}.Of"/> factory,
/// specifically the <c>TotalPages</c> derivation including the exact-multiple and division-guard cases.
/// </summary>
public class PagedResponseTests
{
    [Theory]
    [InlineData(45, 20, 3)]   // partial last page rounds up
    [InlineData(40, 20, 2)]   // exact multiple
    [InlineData(0, 20, 0)]    // no items -> no pages
    [InlineData(1, 20, 1)]    // single item still one page
    public void ComputesTotalPages(long totalItems, int size, int expectedPages)
    {
        var response = PagedResponse<string>.Of(Array.Empty<string>(), 0, size, totalItems);
        Assert.Equal(expectedPages, response.TotalPages);
    }

    [Fact]
    public void GuardsAgainstNonPositivePageSize() =>
        // size <= 0 would divide by zero; the factory must clamp TotalPages to 0 instead.
        Assert.Equal(0, PagedResponse<string>.Of(Array.Empty<string>(), 0, 0, 10).TotalPages);

    [Fact]
    public void CarriesItemsAndPaginationMetadata()
    {
        string[] items = ["a", "b"];
        var response = PagedResponse<string>.Of(items, 2, 20, 100);
        Assert.Equal(items, response.Items);
        Assert.Equal(2, response.Page);
        Assert.Equal(20, response.Size);
        Assert.Equal(100, response.TotalItems);
        Assert.Equal(5, response.TotalPages);
    }
}
