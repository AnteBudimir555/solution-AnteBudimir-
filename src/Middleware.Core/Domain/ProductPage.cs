using System.ComponentModel;

namespace Middleware.Core.Domain;

/// <summary>
/// A page of products plus the pagination metadata reported by the source.
/// <para><c>Limit</c> of <c>0</c> means "all", per the source contract (see <see cref="Abstractions.IProductSource"/>).</para>
/// <para>Cached directly by <c>ProductQueryCache</c>, so it carries the same immutability contract as
/// <see cref="Product"/>: <c>Items</c> must be a genuinely immutable collection, not a read-only view
/// over a mutable one.</para>
/// </summary>
[ImmutableObject(true)]
public sealed record ProductPage(IReadOnlyList<Product> Items, int Total, int Skip, int Limit);
