namespace Middleware.Infrastructure.Upstream.Dto;

/// <summary>DummyJSON paginated list envelope (<c>products</c>, <c>total</c>, <c>skip</c>, <c>limit</c>).</summary>
internal sealed record DummyProductList(
    IReadOnlyList<DummyProduct>? Products,
    int Total,
    int Skip,
    int Limit);
