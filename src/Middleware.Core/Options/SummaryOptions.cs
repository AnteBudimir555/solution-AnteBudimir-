namespace Middleware.Core.Options;

/// <summary>Product-summary shaping options, bound from <c>Summary</c>. The short description is capped.</summary>
public sealed class SummaryOptions
{
    public const string SectionName = "Summary";

    public int DescriptionMaxLength { get; set; } = 100;
}
