using Middleware.Api.Errors;
using Middleware.Api.Validation;
using Middleware.Core.Dtos;
using Middleware.Core.Exceptions;
using Middleware.Core.Services;

namespace Middleware.Api.Endpoints;

/// <summary>
/// Product endpoints. All list/filter/search responses use the trimmed <see cref="ProductSummaryDto"/>
/// shape; only the single-product endpoint returns the full <see cref="ProductDetailDto"/>.
///
/// <para>Every parameter is bound as a raw string and converted in <see cref="QueryParsing"/> so a
/// non-numeric value produces the API's ProblemDetail rather than the framework's empty-bodied 400 —
/// see the note there.</para>
/// </summary>
internal static class ProductEndpoints
{
    public static RouteGroupBuilder MapProductEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/products")
            .WithTags("Products")
            .RequireAuthorization();

        group.MapGet("", ListAsync)
            .WithName("ListProducts")
            .WithSummary("List products")
            .WithDescription("Paginated trimmed product list");

        group.MapGet("/filter", FilterAsync)
            .WithName("FilterProducts")
            .WithSummary("Filter products")
            .WithDescription("Filter by category and/or price range (combinable)");

        group.MapGet("/search", SearchAsync)
            .WithName("SearchProducts")
            .WithSummary("Search products by name")
            .WithDescription("Free-text search over product names");

        group.MapGet("/categories", CategoriesAsync)
            .WithName("ListCategories")
            .WithSummary("List categories")
            .WithDescription("Available product category identifiers");

        // Mapped last so the literal sub-paths above always win the route match.
        group.MapGet("/{id}", GetByIdAsync)
            .WithName("GetProductById")
            .WithSummary("Product details")
            .WithDescription("Full detail of a single product by id");

        return group;
    }

    private static Task<PagedResponse<ProductSummaryDto>> ListAsync(
        string? page, string? size, ProductService service, CancellationToken ct)
    {
        var request = new ListRequest(
            QueryParsing.Int(page, "page", 0),
            QueryParsing.Int(size, "size", 20));
        RequestValidation.EnsureValidParameters(RequestValidators.List, request, "list");

        return service.ListAsync(request.Page, request.Size, ct);
    }

    private static Task<ProductDetailDto> GetByIdAsync(
        string id, ProductService service, CancellationToken ct)
    {
        var request = new ProductIdRequest(QueryParsing.Long(id, "id"));
        RequestValidation.EnsureValidParameters(RequestValidators.ProductId, request, "getById");

        return service.GetByIdAsync(request.Id, ct);
    }

    private static Task<PagedResponse<ProductSummaryDto>> FilterAsync(
        string? category, string? minPrice, string? maxPrice, string? page, string? size,
        ProductService service, CancellationToken ct)
    {
        var request = new FilterRequest(
            category,
            QueryParsing.Decimal(minPrice, "minPrice"),
            QueryParsing.Decimal(maxPrice, "maxPrice"),
            QueryParsing.Int(page, "page", 0),
            QueryParsing.Int(size, "size", 20));
        RequestValidation.EnsureValidParameters(RequestValidators.Filter, request, "filter");

        if (request.MinPrice is { } min && request.MaxPrice is { } max && min > max)
        {
            throw new InvalidRequestException("minPrice must not be greater than maxPrice");
        }

        return service.FilterAsync(request.Category, request.MinPrice, request.MaxPrice, request.Page, request.Size, ct);
    }

    private static Task<PagedResponse<ProductSummaryDto>> SearchAsync(
        string? q, string? page, string? size, ProductService service, CancellationToken ct)
    {
        // Omitted entirely and supplied blank are distinct failures upstream, and stay distinct here.
        if (q is null)
        {
            throw new MissingParameterException("q");
        }

        var request = new SearchRequest(
            q,
            QueryParsing.Int(page, "page", 0),
            QueryParsing.Int(size, "size", 20));
        RequestValidation.EnsureValidParameters(RequestValidators.Search, request, "search");

        return service.SearchByNameAsync(request.Q, request.Page, request.Size, ct);
    }

    private static Task<IReadOnlyList<string>> CategoriesAsync(ProductService service, CancellationToken ct) =>
        service.CategoriesAsync(ct);
}
