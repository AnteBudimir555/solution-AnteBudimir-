namespace Middleware.ShadowHarness;

/// <summary>How a case authenticates. Each service is driven with its own token, since the two mint
/// their own; the harness compares what the request produces, not the credential that carried it.</summary>
internal enum Auth
{
    /// <summary>A freshly issued token from the service under test.</summary>
    Valid,

    /// <summary>No <c>Authorization</c> header at all.</summary>
    None,

    /// <summary>A syntactically broken bearer token.</summary>
    Malformed,

    /// <summary>A well-formed HS256 token signed with the shared dev secret, expired an hour ago.</summary>
    Expired
}

/// <summary>
/// A divergence that has been investigated and accepted, so it does not fail the run. Every entry needs
/// a reason that says why parity is not achievable — not merely that it has not been achieved. The case
/// stays in the corpus and the report still shows the difference; only the exit code ignores it.
/// </summary>
internal sealed record KnownDivergence(string CaseName, string Reason);

/// <summary>One replayed request. <see cref="Name"/> is what the report identifies a diff by.</summary>
internal sealed record ShadowCase(
    string Group,
    string Name,
    string Method,
    string PathAndQuery,
    Auth Auth = Auth.Valid,
    string? Body = null);

/// <summary>
/// The fixed corpus replayed against both services: every endpoint crossed with edge pages, filter
/// combinations and error cases, per the migration plan's parity strategy. Cases are deliberately
/// enumerated rather than generated, so a diff always points at a named, reproducible request.
/// </summary>
internal static class Corpus
{
    private const string Login = "/api/auth/login";

    public static IReadOnlyList<ShadowCase> All() =>
    [
        // --- authentication ------------------------------------------------
        // The token itself differs between services by construction, so the harness masks it and
        // compares the rest of the envelope (tokenType, expiresInSeconds).
        new("auth", "login-valid", "POST", Login, Auth.None, """{"username":"demo","password":"demo1234"}"""),
        new("auth", "login-bad-password", "POST", Login, Auth.None, """{"username":"demo","password":"wrong"}"""),
        new("auth", "login-unknown-user", "POST", Login, Auth.None, """{"username":"nobody","password":"whatever"}"""),
        new("auth", "login-blank-username", "POST", Login, Auth.None, """{"username":"","password":"demo1234"}"""),
        new("auth", "login-missing-password", "POST", Login, Auth.None, """{"username":"demo"}"""),
        new("auth", "login-malformed-json", "POST", Login, Auth.None, """{"username":"demo",,}"""),

        // --- authorization -------------------------------------------------
        new("authz", "list-no-token", "GET", "/api/products", Auth.None),
        new("authz", "list-malformed-token", "GET", "/api/products", Auth.Malformed),
        new("authz", "list-expired-token", "GET", "/api/products", Auth.Expired),
        new("authz", "detail-no-token", "GET", "/api/products/1", Auth.None),
        new("authz", "search-no-token", "GET", "/api/products/search?q=phone", Auth.None),
        new("authz", "categories-no-token", "GET", "/api/products/categories", Auth.None),

        // --- list: edge pages ----------------------------------------------
        new("list", "list-defaults", "GET", "/api/products"),
        new("list", "list-page-0", "GET", "/api/products?page=0&size=20"),
        new("list", "list-size-1", "GET", "/api/products?page=0&size=1"),
        new("list", "list-size-max", "GET", "/api/products?page=0&size=100"),
        new("list", "list-second-page", "GET", "/api/products?page=1&size=20"),
        // DummyJSON holds ~194 products, so this is the last populated page at size 20 ...
        new("list", "list-last-page", "GET", "/api/products?page=9&size=20"),
        // ... and this one is past the end: an empty page, not an error.
        new("list", "list-past-end", "GET", "/api/products?page=50&size=20"),
        new("list", "list-page-max", "GET", "/api/products?page=10000&size=20"),

        // --- list: invalid parameters --------------------------------------
        new("list", "list-page-negative", "GET", "/api/products?page=-1"),
        new("list", "list-page-over-max", "GET", "/api/products?page=10001"),
        new("list", "list-size-zero", "GET", "/api/products?size=0"),
        new("list", "list-size-negative", "GET", "/api/products?size=-5"),
        new("list", "list-size-over-max", "GET", "/api/products?size=101"),
        new("list", "list-page-and-size-invalid", "GET", "/api/products?page=-1&size=0"),
        new("list", "list-page-non-numeric", "GET", "/api/products?page=abc"),
        new("list", "list-size-non-numeric", "GET", "/api/products?size=1.5"),
        new("list", "list-page-empty", "GET", "/api/products?page="),

        // --- detail --------------------------------------------------------
        new("detail", "detail-first", "GET", "/api/products/1"),
        new("detail", "detail-mid", "GET", "/api/products/42"),
        new("detail", "detail-unknown", "GET", "/api/products/999999"),
        new("detail", "detail-zero", "GET", "/api/products/0"),
        new("detail", "detail-negative", "GET", "/api/products/-1"),
        new("detail", "detail-non-numeric", "GET", "/api/products/abc"),
        new("detail", "detail-overflow", "GET", "/api/products/99999999999999999999"),

        // --- filter: combinations -------------------------------------------
        new("filter", "filter-none", "GET", "/api/products/filter"),
        new("filter", "filter-category-only", "GET", "/api/products/filter?category=beauty"),
        new("filter", "filter-category-cased", "GET", "/api/products/filter?category=BEAUTY"),
        new("filter", "filter-category-padded", "GET", "/api/products/filter?category=%20beauty%20"),
        new("filter", "filter-category-unknown", "GET", "/api/products/filter?category=does-not-exist"),
        new("filter", "filter-category-blank", "GET", "/api/products/filter?category="),
        new("filter", "filter-min-only", "GET", "/api/products/filter?minPrice=100"),
        new("filter", "filter-max-only", "GET", "/api/products/filter?maxPrice=50"),
        new("filter", "filter-both-bounds", "GET", "/api/products/filter?minPrice=10&maxPrice=100"),
        new("filter", "filter-bounds-equal", "GET", "/api/products/filter?minPrice=9.99&maxPrice=9.99"),
        new("filter", "filter-bounds-zero", "GET", "/api/products/filter?minPrice=0&maxPrice=0"),
        new("filter", "filter-category-and-bounds", "GET", "/api/products/filter?category=beauty&minPrice=5&maxPrice=20"),
        new("filter", "filter-scale-equivalent", "GET", "/api/products/filter?minPrice=10.00&maxPrice=100.0"),
        new("filter", "filter-high-precision", "GET", "/api/products/filter?minPrice=9.995&maxPrice=10.005"),
        new("filter", "filter-bounds-exclude-all", "GET", "/api/products/filter?minPrice=99999&maxPrice=999999"),

        // --- filter: paging through a price-filtered set ---------------------
        new("filter", "filter-paged-page-0", "GET", "/api/products/filter?minPrice=10&maxPrice=100&page=0&size=5"),
        new("filter", "filter-paged-page-1", "GET", "/api/products/filter?minPrice=10&maxPrice=100&page=1&size=5"),
        new("filter", "filter-paged-past-end", "GET", "/api/products/filter?minPrice=10&maxPrice=100&page=500&size=5"),

        // --- filter: invalid parameters ---------------------------------------
        new("filter", "filter-min-over-max", "GET", "/api/products/filter?minPrice=100&maxPrice=10"),
        new("filter", "filter-min-negative", "GET", "/api/products/filter?minPrice=-1"),
        new("filter", "filter-max-negative", "GET", "/api/products/filter?maxPrice=-1"),
        new("filter", "filter-price-non-numeric", "GET", "/api/products/filter?minPrice=cheap"),
        new("filter", "filter-category-too-long", "GET", "/api/products/filter?category=" + new string('x', 101)),
        new("filter", "filter-category-at-limit", "GET", "/api/products/filter?category=" + new string('x', 100)),
        new("filter", "filter-invalid-page", "GET", "/api/products/filter?category=beauty&page=-1"),

        // --- search ----------------------------------------------------------
        new("search", "search-hit", "GET", "/api/products/search?q=phone"),
        new("search", "search-cased", "GET", "/api/products/search?q=PHONE"),
        new("search", "search-padded", "GET", "/api/products/search?q=%20phone%20"),
        new("search", "search-no-hit", "GET", "/api/products/search?q=zzzzzznotathing"),
        new("search", "search-paged", "GET", "/api/products/search?q=a&page=1&size=5"),
        new("search", "search-past-end", "GET", "/api/products/search?q=phone&page=100&size=20"),
        new("search", "search-at-limit", "GET", "/api/products/search?q=" + new string('x', 100)),

        // --- search: invalid parameters ---------------------------------------
        new("search", "search-missing-q", "GET", "/api/products/search"),
        new("search", "search-blank-q", "GET", "/api/products/search?q="),
        new("search", "search-whitespace-q", "GET", "/api/products/search?q=%20%20"),
        new("search", "search-q-too-long", "GET", "/api/products/search?q=" + new string('x', 101)),
        new("search", "search-invalid-size", "GET", "/api/products/search?q=phone&size=0"),

        // --- categories --------------------------------------------------------
        new("categories", "categories", "GET", "/api/products/categories"),
        // Query parameters are not part of the contract here; both must ignore them.
        new("categories", "categories-with-noise", "GET", "/api/products/categories?page=3"),

        // --- routing -----------------------------------------------------------
        new("routing", "unknown-path", "GET", "/api/products/does/not/exist"),
        new("routing", "unknown-root-path", "GET", "/nope"),
        new("routing", "wrong-method-on-list", "POST", "/api/products"),
        new("routing", "wrong-method-on-login", "GET", Login, Auth.None),
        new("routing", "trailing-slash-on-list", "GET", "/api/products/")
    ];

    public static IReadOnlyList<KnownDivergence> KnownDivergences() =>
    [
        new("list-page-and-size-invalid",
            "Both services report the same two constraint messages; only the order they are joined in "
            + "differs. Spring joins them in the iteration order of Hibernate Validator's violation "
            + "Set, which is unordered — across the endpoints it comes out as size/page, page/size and "
            + "size/q respectively, following neither declaration nor alphabetical order. Reproducing "
            + "it would mean emulating Java's hash layout, so the .NET side joins in parameter "
            + "declaration order. Requests violating a single constraint — every other case here — "
            + "match exactly.")
    ];
}
