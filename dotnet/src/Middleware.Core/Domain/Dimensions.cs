using System.ComponentModel;

namespace Middleware.Core.Domain;

/// <summary>
/// Physical dimensions of a product.
/// <para>Reachable from a cached <see cref="Product"/>, so it carries the same immutability contract.</para>
/// </summary>
[ImmutableObject(true)]
public sealed record Dimensions(double? Width, double? Height, double? Depth);
