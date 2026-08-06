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
///   (<c>/products/search</c>, <c>/products/category/{slug}</c>) — one or the other, never both in a
///   single call, because DummyJSON has no endpoint that intersects them.</item>
///   <item>Pagination uses the upstream <c>limit</c>/<c>skip</c> parameters.</item>
///   <item>Price-range filtering is <em>not</em> supported by DummyJSON, so
///   <see cref="SupportsPriceFilter"/> is <c>false</c> and every result reports
///   <c>PriceFilterApplied: false</c>; the caller applies the bounds over what comes back.</item>
/// </list>
///
/// <para>Wired as a typed <see cref="HttpClient"/> whose base address, timeouts and resilience
/// pipeline come from <c>UpstreamOptions</c>, so a slow or unreachable upstream fails fast (surfacing
/// as a 502) rather than hanging the request. Retries, the circuit breaker and the timeouts all live
/// in that pipeline, below this type: a transient blip is retried before <see cref="FetchAsync"/>
/// ever sees it, and a shed request arrives here as an exception it translates like any other.</para>
/// </summary>
public sealed class DummyJsonProductSource(HttpClient httpClient, ILogger<DummyJsonProductSource> logger)
    : IProductSource
{
    private static readonly ProductPage EmptyPage = new([], 0, 0, 0);

    /// <summary>
    /// DummyJSON has no price parameter on any of its endpoints, so the bounds are never applied here
    /// and the caller filters in memory. This is the whole reason
    /// <c>ProductQueryCache</c> fetches an unfiltered candidate set for the price path.
    /// </summary>
    public bool SupportsPriceFilter => false;

    public async Task<ProductQueryResult> QueryAsync(ProductQuery query, CancellationToken ct = default)
    {
        // "Everything" is null here and limit=0 on the wire — the translation is DummyJSON's own
        // convention and stops at this line. A limit that is present must be a real page size: 0 used
        // to mean "the entire catalog" by accident of that convention, which turned an arithmetic slip
        // into a full download.
        if (query.Limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query), query.Limit,
                "Limit must be null (meaning every match) or greater than zero.");
        }
        var limit = query.Limit ?? 0;

        var hasName = !string.IsNullOrEmpty(query.NameContains);
        var hasCategory = !string.IsNullOrEmpty(query.Category);
        if (hasName && hasCategory)
        {
            // The query object can express this; DummyJSON cannot answer it — it has a search endpoint
            // and a category endpoint and no way to intersect them. Refusing beats picking one filter
            // and quietly dropping the other, which would return a superset the caller believes is
            // exact. Nothing constructs such a query today; a source that can serve it may.
            throw new NotSupportedException(
                "DummyJSON cannot filter by category and name in one call; issue them separately.");
        }

        var (description, uri) = (hasName, hasCategory) switch
        {
            (true, _) => ($"search products for {query.NameContains}",
                $"products/search?q={Uri.EscapeDataString(query.NameContains!)}&limit={limit}&skip={query.Skip}"),
            (_, true) => ($"filter by category {query.Category}",
                $"products/category/{Uri.EscapeDataString(query.Category!)}?limit={limit}&skip={query.Skip}"),
            _ => ("list products", $"products?limit={limit}&skip={query.Skip}")
        };

        logger.LogDebug("Upstream query: category={Category}, name={Name}, skip={Skip}, limit={Limit}",
            query.Category, query.NameContains, query.Skip, query.Limit);

        var body = await FetchAsync<DummyProductList>(description, uri, notFoundId: null, ct);
        return new ProductQueryResult(ToPage(body), PriceFilterApplied: false);
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
