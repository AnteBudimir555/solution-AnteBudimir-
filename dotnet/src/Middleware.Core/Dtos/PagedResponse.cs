namespace Middleware.Core.Dtos;

/// <summary>
/// Generic pagination envelope returned by the list/filter/search endpoints.
/// </summary>
/// <param name="Items">the items on the current page</param>
/// <param name="Page">zero-based page index</param>
/// <param name="Size">requested page size</param>
/// <param name="TotalItems">total number of matching items across all pages</param>
/// <param name="TotalPages">total number of pages for the given size</param>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int Size,
    long TotalItems,
    int TotalPages)
{
    public static PagedResponse<T> Of(IReadOnlyList<T> items, int page, int size, long totalItems)
    {
        int totalPages = size <= 0 ? 0 : (int)Math.Ceiling((double)totalItems / size);
        return new PagedResponse<T>(items, page, size, totalItems, totalPages);
    }
}
