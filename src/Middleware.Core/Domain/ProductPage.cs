using System.ComponentModel;

namespace Middleware.Core.Domain;

/// <summary>
/// A page of products plus the pagination metadata reported by the source.
/// <para><c>Skip</c>/<c>Limit</c> are what the source reported for the page it returned, which is not
/// always what was asked for — a request for every match (<see cref="Abstractions.ProductQuery.Limit"/>
/// of <c>null</c>) comes back describing whatever the source chose to serve.</para>
/// <para>Cached directly by <c>ProductQueryCache</c>, so it carries the same immutability contract as
/// <see cref="Product"/>: <c>Items</c> must be a genuinely immutable collection, not a read-only view
/// over a mutable one.</para>
/// </summary>
[ImmutableObject(true)]
public sealed record ProductPage(IReadOnlyList<Product> Items, int Total, int Skip, int Limit);
