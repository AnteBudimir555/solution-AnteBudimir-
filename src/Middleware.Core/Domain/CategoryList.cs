using System.ComponentModel;

namespace Middleware.Core.Domain;

/// <summary>
/// The set of category identifiers the source exposes, wrapped in a record purely so it can be cached
/// without paying a deserialization per hit.
///
/// <para><b>The wrapper is not ceremony.</b> <c>HybridCache</c> returns the stored instance only for a
/// type it can prove immutable, and that proof is carried by <see cref="ImmutableObjectAttribute"/> on
/// a concrete type. Measured against <c>Microsoft.Extensions.Caching.Hybrid</c> 10.8.0, caching the
/// bare collection returns a <em>different</em> instance on each hit for every obvious candidate —
/// <c>IReadOnlyList&lt;string&gt;</c>, <c>string[]</c>, an unmarked record, and (the one that looks
/// safest and is not) <c>ImmutableArray&lt;string&gt;</c>. Only the marked record is handed back as the
/// same instance.</para>
///
/// <para>As with <see cref="Product"/>, the attribute makes the promise load-bearing:
/// <see cref="Names"/> must be a genuinely immutable collection rather than a read-only view over a
/// mutable one, because every caller shares this instance.</para>
/// </summary>
[ImmutableObject(true)]
public sealed record CategoryList(IReadOnlyList<string> Names);
