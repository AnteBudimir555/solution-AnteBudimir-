using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Middleware.Core.Abstractions;
using Middleware.Core.Domain;
using Middleware.Core.Exceptions;
using Middleware.Infrastructure.Upstream.Dto;

namespace Middleware.Infrastructure.Upstream;

/// <summary>
/// <see cref="IProductSource"/> backed by the DummyJSON REST API.
///
/// <para>Filter/search push-down policy (documented in the README):</para>
/// <list type="bullet">
///   <item>Name search and category filtering are pushed down to the upstream
///   (<c>/products/search</c>, <c>/products/category/{slug}</c>).</item>
///   <item>Pagination uses the upstream <c>limit</c>/<c>skip</c> parameters.</item>
///   <item>Price-range filtering is <em>not</em> supported by DummyJSON, so it is applied in-service
///   (see <c>ProductService</c>); this source only exposes the primitives.</item>
/// </list>
///
/// <para>Wired as a typed <see cref="HttpClient"/> whose base address and connect/response timeouts
/// come from <c>UpstreamOptions</c>, so a slow or unreachable upstream fails fast (surfacing as a 502)
/// rather than hanging the request.</para>
/// </summary>
public sealed class DummyJsonProductSource(HttpClient httpClient, ILogger<DummyJsonProductSource> logger)
    : IProductSource
{
    private static readonly ProductPage EmptyPage = new([], 0, 0, 0);

    public async Task<ProductPage> ListAsync(int skip, int limit, CancellationToken ct = default)
    {
        logger.LogDebug("Upstream list: skip={Skip}, limit={Limit}", skip, limit);
        var body = await FetchAsync<DummyProductList>(
            "list products", $"products?limit={limit}&skip={skip}", notFoundId: null, ct);
        return ToPage(body);
    }

    public async Task<Product> GetByIdAsync(long id, CancellationToken ct = default)
    {
        logger.LogDebug("Upstream getById: id={Id}", id);
        var body = await FetchAsync<DummyProduct>(
            $"get product {id}", $"products/{id}", notFoundId: id, ct);
        if (body is null)
        {
            throw new UpstreamException($"Upstream returned an empty body for product {id}");
        }
        return DummyProductMapper.ToDomain(body);
    }

    public async Task<ProductPage> FindByCategoryAsync(string category, int skip, int limit, CancellationToken ct = default)
    {
        logger.LogDebug("Upstream findByCategory: category={Category}, skip={Skip}, limit={Limit}", category, skip, limit);
        var body = await FetchAsync<DummyProductList>(
            $"filter by category {category}",
            $"products/category/{Uri.EscapeDataString(category)}?limit={limit}&skip={skip}",
            notFoundId: null, ct);
        return ToPage(body);
    }

    public async Task<ProductPage> SearchByNameAsync(string query, int skip, int limit, CancellationToken ct = default)
    {
        logger.LogDebug("Upstream searchByName: q={Query}, skip={Skip}, limit={Limit}", query, skip, limit);
        var body = await FetchAsync<DummyProductList>(
            "search products",
            $"products/search?q={Uri.EscapeDataString(query)}&limit={limit}&skip={skip}",
            notFoundId: null, ct);
        return ToPage(body);
    }

    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default)
    {
        logger.LogDebug("Upstream categories");
        var body = await FetchAsync<string[]>(
            "list categories", "products/category-list", notFoundId: null, ct);
        return body ?? [];
    }

    private static ProductPage ToPage(DummyProductList? body)
    {
        if (body?.Products is null)
        {
            return EmptyPage;
        }
        // Frozen, not just projected: the page is cached and shared across concurrent requests
        // (see the immutability contract on ProductPage).
        var items = DummyProductMapper.Freeze(body.Products.Select(DummyProductMapper.ToDomain))!;
        return new ProductPage(items, body.Total, body.Skip, body.Limit);
    }

    /// <summary>
    /// Executes an upstream GET and normalizes failures: a 404 with a <paramref name="notFoundId"/>
    /// becomes <see cref="ProductNotFoundException"/>, any other non-success status or transport
    /// failure (timeout, connection error) becomes <see cref="UpstreamException"/>. The two typed
    /// exceptions propagate as-is if thrown deeper.
    /// </summary>
    private async Task<T?> FetchAsync<T>(string description, string uri, long? notFoundId, CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync(uri, ct);

            if (notFoundId is not null && response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ProductNotFoundException(notFoundId.Value);
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Upstream error during {Description}: {Status}", description, (int)response.StatusCode);
                throw new UpstreamException($"DummyJSON returned {(int)response.StatusCode} {response.StatusCode}");
            }

            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (ProductNotFoundException)
        {
            throw;
        }
        catch (UpstreamException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Upstream call failed during {Description}: {Message}", description, e.Message);
            throw new UpstreamException($"Upstream call failed during {description}", e);
        }
    }
}
