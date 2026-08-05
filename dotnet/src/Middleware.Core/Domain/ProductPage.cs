namespace Middleware.Core.Domain;

/// <summary>
/// A page of products plus the pagination metadata reported by the source.
/// <para><c>Limit</c> of <c>0</c> means "all", per the source contract (see <see cref="Abstractions.IProductSource"/>).</para>
/// </summary>
public sealed record ProductPage(IReadOnlyList<Product> Items, int Total, int Skip, int Limit);
