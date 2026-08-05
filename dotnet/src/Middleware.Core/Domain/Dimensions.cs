namespace Middleware.Core.Domain;

/// <summary>Physical dimensions of a product.</summary>
public sealed record Dimensions(double? Width, double? Height, double? Depth);
