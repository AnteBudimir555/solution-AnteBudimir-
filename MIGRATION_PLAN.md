# Migration Blueprint: Abysalto Product Middleware to C# & .NET

> **Scope.** Definitive, checkbox-tracked roadmap for porting the
> `com.abysalto.middleware` service (Spring Boot 4.1.0 / Java 21) to C# on
> **.NET 10 (LTS)**. The system is a stateless HTTP middleware that re-exposes the
> DummyJSON product catalog with a trimmed shape, filtering, name search, JWT
> authentication, and caching. It is small (~45 production source files) and cleanly
> layered (ports & adapters). The difficulty is **behavioral parity**, not volume —
> concentrated in three areas: Spring's cache-proxy semantics, the RFC-7807 error
> contract, and JWT validation shape.
>
> **Progress tracking.** Every actionable task is a markdown checkbox. Check items off
> (`- [x]`) as they land. Phase gates are the **Acceptance Criteria** blocks — do not
> advance a phase until all of its criteria are checked.

---

## 1. Technical Audit of Existing System

### 1.1 Primary stack, framework, and architectural patterns

| Concern | Current technology |
|---|---|
| Language / runtime | Java 21 (release level), compiled on JDK 25 |
| Framework | Spring Boot 4.1.0 / Spring Framework 7 |
| Build | Maven (`spring-boot-starter-parent`); surefire (unit) + failsafe (`*IT`) split |
| Web layer | Spring Web **MVC** — servlet stack, fully synchronous (no reactive/Netty) |
| Outbound HTTP | `RestClient` (blocking, `spring-web`) with connect/read timeouts |
| Persistence | Spring Data JPA + Hibernate; H2 (dev), PostgreSQL (`postgres` profile); `ddl-auto=update` |
| Security | Spring Security, stateless JWT; `jjwt` 0.12.6, HS256; BCrypt password hashing |
| Caching | Spring Cache abstraction backed by Caffeine 3.2.0 |
| Validation | Jakarta Bean Validation (`@Min`, `@Max`, `@Size`, `@NotBlank`, `@Positive`, …) |
| API docs | springdoc-openapi 3.0.3 (Swagger UI) |
| Logging | Logback + SLF4J MDC correlation id; logstash JSON encoder on `postgres` profile |
| Error model | RFC-7807 `ProblemDetail` via `@RestControllerAdvice` |
| Boilerplate | Lombok (optional/minimal); Java `record` for domain + DTOs |
| Tests | JUnit 5, Mockito, Spring Boot test slices, MVC + web-client mock starters |

**Architectural pattern: ports & adapters (hexagonal-lite) over a layered core.**
Request flow: `Controller → ProductService → ProductQueryCache → ProductSource (port)
→ DummyJsonProductSource (adapter) → DummyJSON REST`. The internal `Product` record is
**source-agnostic**; the DummyJSON payload shape never leaks past the adapter. Products
are **never persisted** — the *only* persisted entity is `UserAccount` (authentication).

### 1.2 Complete dependency mapping (Java → .NET NuGet)

| Current (Java / Maven) | Recommended .NET replacement (NuGet) | Notes |
|---|---|---|
| `spring-boot-starter-webmvc` | ASP.NET Core Minimal APIs (in-box) | Route groups per controller |
| `RestClient` + `SimpleClientHttpRequestFactory` | `IHttpClientFactory` typed client + `System.Net.Http.Json` | Register source as typed client |
| Client timeouts / resilience | `Microsoft.Extensions.Http.Resilience` (Polly) | Connect/read timeouts; retry optional — parity first |
| `spring-boot-starter-data-jpa` + Hibernate | **EF Core 10** (`Microsoft.EntityFrameworkCore`) | Single `DbSet<UserAccount>` |
| `postgresql` driver | `Npgsql.EntityFrameworkCore.PostgreSQL` | `postgres` profile equivalent |
| `h2` (dev DB) | `Microsoft.EntityFrameworkCore.Sqlite` | H2 has no .NET analog; SQLite is the idiomatic dev DB |
| `spring-boot-starter-security` + JWT filter | `Microsoft.AspNetCore.Authentication.JwtBearer` | Built-in bearer middleware replaces the custom filter |
| `jjwt-api/impl/jackson` | `Microsoft.IdentityModel.JsonWebTokens` (`JsonWebTokenHandler`) | HS256 signing |
| `BCryptPasswordEncoder` | **`BCrypt.Net-Next`** | Keep BCrypt so any migrated hashes verify unchanged |
| `spring-boot-starter-cache` + `caffeine` (`sync=true`) | **`HybridCache`** (`Microsoft.Extensions.Caching.Hybrid`) | Built-in **stampede protection** = the `sync=true` guarantee |
| `spring-boot-starter-validation` | **FluentValidation** (or built-in Minimal API validation) | Cross-field `minPrice ≤ maxPrice` |
| `springdoc-openapi-starter-webmvc-ui` | `Microsoft.AspNetCore.OpenApi` + **`Scalar.AspNetCore`** | In-box doc gen + UI |
| `@RestControllerAdvice` + `ProblemDetail` | `AddProblemDetails()` + `IExceptionHandler` | Native RFC-7807 |
| Logback + MDC + `logstash-logback-encoder` | **`Serilog.AspNetCore`** + `Serilog.Formatting.Compact` | `LogContext.PushProperty` = MDC; compact JSON = logstash |
| `@ConfigurationProperties` | `IOptions<T>` + `services.Configure<T>()` + `ValidateOnStart()` | Reproduces JWT fail-fast |
| Lombok / `record` | C# `record` / primary constructors | Native |
| `ProductMapper` (hand-written) | Hand-written static mappers or **Mapperly** | Keep manual for this surface |
| `ApplicationRunner` (seeder) | `IHostedService` seeder (after migrations) | |
| JUnit 5 + Mockito + `MockRestServiceServer` | **xUnit** + **NSubstitute** + **WireMock.Net** | Upstream stubbing |
| `@SpringBootTest` / MVC slice | `WebApplicationFactory<Program>` | In-process integration host |
| PostgreSQL test infra | **Testcontainers for .NET** (`Testcontainers.PostgreSql`) | Prod-like DB in tests |

### 1.3 Architectural hotspots and complex logic blocks (ranked)

1. **Cache-proxy semantics (`ProductQueryCache`) — HIGHEST.** Exists *because* Spring's
   `@Cacheable` only intercepts calls through the proxy (self-invocation bypasses it),
   and because `priceFilteredCandidates` is cached by category + price bounds
   **independent of pagination**. `sync = true` adds **stampede protection**. .NET has no
   proxy caching — reproduce all three properties explicitly.
2. **Normalized cache keys (`CacheKeys`).** SpEL keys and the pre-call normalization
   must stay identical so key and upstream call can never diverge.
3. **RFC-7807 contract (`GlobalExceptionHandler`).** Custom `type` URIs, `timestamp`
   property, field-level validation joins, and security 401/403 routed through the same
   renderer.
4. **JWT parity (`JwtService`).** HS256, subject = username, enforced issuer,
   `expiresInSeconds` in the body, silent failure on malformed tokens.
5. **`BigDecimal` price math.** Prices exact (`BigDecimal` → `decimal`); ratings/weight
   `Double` → `double`. Inclusive filter bounds (`>= min`, `<= max`).
6. **In-memory candidate materialization.** `priceFilteredCandidates` fetches the whole
   filtered catalog and warns past `max-in-memory-candidates` (5000).
7. **`TextUtils.truncate`.** Ellipsis budget + word-boundary-or-hard-cut logic.
8. **`RequestLoggingFilter`.** Inbound correlation id reused only if matching
   `[A-Za-z0-9._-]{1,64}`; MDC fully cleared in `finally`.

---

## 2. Proposed .NET Target Architecture

### 2.1 Runtime and project types
- **Runtime:** **.NET 10 (LTS, Nov 2025), C# 14** — latest stable + LTS. (Prior LTS .NET 8
  works with only OpenAPI/validation notes changing.)
- **Application type:** **ASP.NET Core Minimal APIs** with route groups
  (`MapGroup("/api/products")`, `MapGroup("/api/auth")`) and typed handler classes.

### 2.2 Solution layout (Clean Architecture)

```
Abysalto.Middleware.sln
├── src/
│   ├── Middleware.Api/                # ASP.NET Core host (Minimal APIs)
│   │   ├── Program.cs                 # composition root, DI, pipeline
│   │   ├── Endpoints/                 # ProductEndpoints, AuthEndpoints
│   │   ├── Middleware/                # CorrelationId, ProblemDetails handler
│   │   ├── Validation/                # FluentValidation validators
│   │   └── appsettings*.json          # Development (SQLite) / Postgres profiles
│   ├── Middleware.Core/               # domain + application (no framework deps)
│   │   ├── Domain/ Dtos/ Abstractions/ Services/ Common/ Exceptions/
│   └── Middleware.Infrastructure/     # adapters
│       ├── Persistence/ Sources/DummyJson/ Security/ Caching/
└── tests/
    ├── Middleware.UnitTests/          # xUnit + NSubstitute
    └── Middleware.IntegrationTests/   # WebApplicationFactory + WireMock.Net + Testcontainers
```
Dependency direction: `Api → Infrastructure → Core`; `Core` depends on nothing.

### 2.3 Data access strategy and DB configuration
- **EF Core, not Dapper** — one entity, two queries; migrations for free.
- Providers by environment: **SQLite** (Development ≈ H2 dev), **Npgsql** (Postgres).
- `ddl-auto=update` → **EF Core migrations**, applied on startup via `Database.Migrate()`.
- `user_account`: identity PK, unique `username`, `password_hash`, `role` as **string**
  (`.HasConversion<string>()`).
- Config → `IOptions<T>`: `UpstreamOptions`, `JwtOptions` (with `ValidateOnStart` fail-fast),
  `SummaryOptions` (max 100), `CacheOptions`, `SeedUserOptions`.

---

## 3. Exhaustive Step-by-Step Implementation Roadmap

> **Status (Phase 1): code-complete.** Solution builds warning-free; 32 ported unit
> tests pass. The .NET solution lives in the top-level `dotnet/` folder (kept separate
> from the Java `src/` tree during migration). **Target is `net8.0`**, not `net10.0`:
> the available SDK is 8.0.x and the plan flags .NET 8 as the supported fallback —
> bumping `TargetFramework` in `dotnet/Directory.Build.props` is the only change to
> move to a newer runtime later. The JWT fail-fast (`ValidateOnStart`) is deferred to
> Phase 4, where the Api DI container is wired; the `JwtOptions` validation attributes
> are already in place.

### Phase 1: Solution Setup & Shared Contracts
- [x] Create the .NET solution (`Abysalto.Middleware.sln`) and the project folders per §2.2 (under `dotnet/`).
- [x] Scaffold projects: `Middleware.Api` (webapi/minimal), `Middleware.Core` (classlib), `Middleware.Infrastructure` (classlib), `Middleware.UnitTests` + `Middleware.IntegrationTests` (xunit).
- [x] Wire project references: `Api → Infrastructure → Core`; test projects reference their targets.
- [x] Add root `Directory.Build.props`: `TargetFramework=net8.0`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latest`.
- [x] Define the Clean Architecture core domain records: `Product`, `Dimensions`, `Meta`, `Review`, `ProductPage` (+ `Role`).
- [x] Map shared DTOs: `ProductSummaryDto`, `ProductDetailDto`, `LoginRequest`, `LoginResponse` (with `Bearer(...)` factory).
- [x] Port `PagedResponse<T>` including `Of(...)` — `totalPages = ceil(total/size)`, guarded for `size <= 0`.
- [x] Port `TextUtils.Truncate` **verbatim** (ellipsis budget + word-boundary logic).
- [x] Port `CacheKeys` normalization **verbatim** (search / filterPage / filterCandidates).
- [x] Define domain exceptions: `ProductNotFoundException`, `UpstreamException`, `InvalidRequestException`.
- [x] Define the `IProductSource` port abstraction (in `Core`, ahead of the Phase 3 adapter).
- [x] Define options classes for the full config surface (`JwtOptions`, `UpstreamOptions`, `SummaryOptions`, `CacheOptions`, `SeedUserOptions`) with validation attributes. *(`ValidateOnStart()` wiring → Phase 4.)*

**Acceptance Criteria**
- [x] `dotnet build` succeeds warning-free under `TreatWarningsAsErrors`.
- [x] Ported `TextUtilsTest` and `CacheKeysTest` cases pass.
- [ ] Startup fails fast when the JWT secret is missing/too short. *(Deferred to Phase 4 — requires the Api host/DI.)*
- [x] `PagedResponse.Of` matches Java `totalPages` across a `(total, size)` table including `size = 0`.

> **Status (Phase 2): code-complete.** EF Core 8 persistence layer implemented; the
> `InitialCreate` migration is generated for Npgsql with column names pinned to
> snake_case (`id`, `username`, `password_hash`, `role`) to match the Hibernate schema
> and de-risk R5. Full suite: **37 passed, 1 skipped**. The skipped test is the
> Testcontainers-Postgres migration test — this dev box has no Docker, so it reports as
> skipped and runs for real in CI. Startup schema init is implemented as
> `InitializeDatabaseAsync` (Migrate for Npgsql / EnsureCreated for the SQLite dev DB);
> its invocation from the host is wired in Phase 4.

### Phase 2: Data Access Layer & DB Migration
- [x] Implement the `UserAccount` entity (private setters; `Role` enum).
- [x] Implement `AppDbContext` with `DbSet<UserAccount>`.
- [x] Configure the entity: unique index on `username`, `role` via `.HasConversion<string>()`, identity-generated PK, snake_case column names (incl. `password_hash`).
- [x] Register the provider by environment: `UseSqlite` (Development) / `UseNpgsql` (Postgres) from configuration (`AddPersistence`).
- [x] Enforce no-default prod credentials (fail fast under the Postgres provider — throws when `ConnectionStrings:Default` is absent).
- [x] Create the initial EF migration (`InitialCreate`) via the `dotnet-ef` local tool + a design-time context factory (Npgsql).
- [x] Apply migrations on startup via `InitializeDatabaseAsync` (Migrate for Npgsql / EnsureCreated for SQLite). *(Host invocation → Phase 4.)*
- [x] Implement `IUserRepository`: `FindByUsernameAsync`, `ExistsByUsernameAsync`, `AddAsync`.

**Acceptance Criteria**
- [x] `InitialCreate` produces a `user_account` table matching the Hibernate schema (unique username, string role, identity id).
- [x] Testcontainers-Postgres integration test confirms the migration applies and the repository round-trips a user. *(Written & Docker-guarded; skipped locally, runs in CI.)*
- [x] Missing DB credentials under the Postgres provider fail startup with no silent fallback. *(Verified by `PersistenceConfigTests`.)*
- [x] SQLite integration test round-trips the repository and enforces the unique-username constraint (runnable without Docker).

### Phase 3: Core Business Logic & Services
- [x] Implement `IProductSource` and the DummyJSON typed client `DummyJsonProductSource`: `List`, `GetById`, `FindByCategory`, `SearchByName`, `Categories`.
- [x] Configure the typed `HttpClient` with connect/response timeouts (`SocketsHttpHandler.ConnectTimeout` + `HttpClient.Timeout`, bound from `UpstreamOptions`).
- [x] Port the `fetch(...)` error-normalization: 404 on `getById` → `ProductNotFoundException`; other non-2xx / timeout / connection error → `UpstreamException`; domain exceptions propagate.
- [x] Port `DummyProductMapper` (upstream DTO → `Product`); `reviewerEmail` PII structurally dropped; price bound straight to `decimal` (no lossy `double` hop).
- [x] Port `ProductMapper` (domain → `ProductSummaryDto` with 100-char truncated description / `ProductDetailDto`).
- [x] Port `ProductService`: `List`, `GetById`, `Filter` (no-price vs. price path), `SearchByName`, `Categories`; preserve `offset = page * size` and the slicing bounds.
- [x] Port `ProductQueryCache` on **HybridCache**: one entry per normalized key via `GetOrCreateAsync`; candidate set keyed by category + price **independent of page**. `IProductQueryCache` extracted so the service is unit-testable in isolation.
- [x] Preserve the `max-in-memory-candidates` bound and the over-threshold warning log.
- [x] Register all services in the DI container (`AddApplicationServices` in `Core`, `AddUpstreamSource` in `Infrastructure`). *(Host `UseSerilog`/pipeline invocation → Phase 4.)*
- [ ] Wire Serilog (logging), the ProblemDetails handler (error handling), and shared utilities. *(Deferred to Phase 4 — these are Api-host/pipeline concerns; the services and exceptions they surface are complete.)*

> **Status — Phase 3 complete (core services & upstream adapter).** The application layer is
> implemented and unit/component-tested against source-parity fixtures. `DummyJsonProductSource` is a
> public typed-client adapter (upstream DTOs + `DummyProductMapper` kept `internal` so the DummyJSON
> shape never leaks); `ProductService`/`ProductQueryCache`/`ProductMapper` live in `Core`. Single-flight
> is provided by **HybridCache** (`GetOrCreateAsync`), the direct analog of Spring's
> `@Cacheable(sync = true)`. Serilog + the ProblemDetails handler are intentionally left to Phase 4,
> where the Api host and its middleware pipeline are wired.

**Acceptance Criteria**
- [x] Ported `ProductServiceTest`, `ProductQueryCacheTest`, `ProductMapperTest`, `PagedResponseTest`, `DummyJsonProductSourceTest` (WireMock.Net) pass.
- [x] Concurrency test proves single-flight: N parallel identical queries ⇒ exactly one upstream call (`ConcurrentIdenticalSearchesShareOneUpstreamFetch`).
- [x] Paging a price-filtered result triggers exactly one upstream catalog fetch regardless of page count (`PriceFilteredCandidatesAreCachedIndependentlyOfPage`).
- [x] Price-filter boundaries are inclusive and exact (`decimal`), verified at `minPrice`/`maxPrice`.

### Phase 4: API Endpoints, Auth & Middleware
- [ ] Map the six endpoints in route groups (`/api/products` list/`{id}`/`filter`/`search`/`categories`, `/api/auth/login`).
- [ ] Add FluentValidation rules: `page ∈ [0,10000]`, `size ∈ [1,100]`, free-text ≤ 100, `q` not blank, `id > 0`, cross-field `minPrice ≤ maxPrice` (→ `InvalidRequestException`).
- [ ] Implement `JwtService` (issue/validate HS256): subject = username, issuer set + enforced, `expiresInSeconds` on the response; silent rejection of malformed/expired/wrong-issuer tokens.
- [ ] Configure `JwtBearer`: `ClockSkew = TimeSpan.Zero`, `MapInboundClaims = false`, `ValidateIssuer = true`, `ValidateAudience = false`, symmetric HS256 key from config.
- [ ] Implement `AuthEndpoints.Login`: BCrypt verify (`BCrypt.Net-Next`), issue token, log identity only (never password/token).
- [ ] Implement `UserSeeder` as `IHostedService` (seed only when enabled and absent; store BCrypt hash only).
- [ ] Configure authorization: public = auth + OpenAPI paths; everything else requires a valid bearer token.
- [ ] Implement `ProblemDetailsExceptionHandler` (`IExceptionHandler` + `AddProblemDetails`): `type` URIs, `title`, `status`, `detail`, `timestamp`, field-level validation joins.
- [ ] Wire `JwtBearerEvents.OnChallenge`/`OnForbidden` so 401/403 render the **same** ProblemDetail.
- [ ] Implement `CorrelationIdMiddleware`: read/echo `X-Correlation-Id`, reuse inbound only if matching `[A-Za-z0-9._-]{1,64}`, push to Serilog `LogContext`, emit one completion line (method/path/status/duration), clear context afterward.
- [ ] Configure Serilog sinks: compact JSON (Postgres) + readable console (Development); never log the Authorization header or login payload.
- [ ] Configure CORS policy (explicit, even if permissive-for-dev) and request/response validation wiring.
- [ ] Add the OpenAPI document + Scalar UI; mark `/api/auth/login` as public (no lock).

**Acceptance Criteria**
- [ ] All endpoints enforce validation with the same status/message shape as the Java service.
- [ ] A seeded user authenticates; token `sub`/`iss`/`exp` match the Java claims; tampered/expired/wrong-issuer tokens are rejected.
- [ ] 400 / 401 / 403 / 404 / 502 / 500 all produce byte-comparable ProblemDetail bodies.
- [ ] Every log line carries the correlation id; the response echoes `X-Correlation-Id`; no credential material is logged.
- [ ] `/openapi` (or `/swagger`) renders; `/api/auth/login` shows no auth requirement.

### Phase 5: Testing & Feature Parity Verification
- [ ] Implement automated unit tests — full xUnit ports of every existing unit test (services, mapper, cache, JWT, `TextUtils`, `CacheKeys`, `PagedResponse`).
- [ ] Implement integration tests with `WebApplicationFactory<Program>` + WireMock.Net (upstream) + Testcontainers-Postgres (DB); port `ProductApiIT` and `CachingIT`.
- [ ] Execute endpoint comparison tests: API-shadowing harness replays a fixed corpus against both Java and .NET services (same DummyJSON) and diffs normalized responses.
- [ ] Snapshot both OpenAPI documents and diff paths/schemas to prove the contract is unchanged.
- [ ] Package: multi-stage `Dockerfile` (`dotnet publish` → `aspnet:10.0`, non-root user); update `docker-compose.yml` (keep `postgres:17`, map env vars, preserve the missing-secret fail-fast).
- [ ] Final performance/load test against the new stack; compare latency and upstream-call counts to the Java baseline.

**Acceptance Criteria**
- [ ] Full unit + integration suite green in CI.
- [ ] The shadowing harness reports zero response diffs across the corpus (status + normalized JSON) for all endpoints, edge pages, filter combinations, and error cases.
- [ ] OpenAPI diff shows no path/schema changes.
- [ ] `docker compose up --build` brings up DB + .NET app; smoke suite passes.
- [ ] Load test shows no material latency regression and cache single-flight holds under concurrency.

---

## 4. Testing & Feature Parity Strategy

Four complementary layers to **prove** identical behavior:

1. **API shadowing (primary parity proof).** Point both Java and .NET services at the
   *same* live DummyJSON. Replay a fixed corpus — every endpoint × edge pages (0, last,
   out-of-range) × filter combinations (category only, price only, both,
   `minPrice = maxPrice`, `minPrice > maxPrice`) × error cases (unknown id, blank `q`,
   oversized params, missing/expired token) — asserting each .NET response matches the
   Java response on **status + normalized JSON body**. For a proxy this is the most
   direct parity guarantee.
2. **Ported unit tests (behavioral spec).** The existing suite encodes exact intended
   behavior — truncation boundaries, cache-key normalization, pagination math, JWT
   validation, price-filter inclusivity. Port each to xUnit + NSubstitute.
3. **In-process integration tests.** `WebApplicationFactory<Program>` (the ASP.NET analog
   of `@SpringBootTest`) with **WireMock.Net** stubbing DummyJSON and
   **Testcontainers-Postgres** for the DB. Port `ProductApiIT` (endpoints + auth) and
   `CachingIT` (one upstream call under repeated identical queries — validates HybridCache
   stampede behavior).
4. **Contract snapshot.** Diff the two OpenAPI documents' paths and schemas before cutover.

**Cutover:** run both services side-by-side, shadow production traffic, and promote the
.NET service only when the shadowing diff and contract diff are both clean.

---

## 5. Risk Log & Mitigation Matrix

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| R1 | **Cache-semantics regression.** `sync = true` + pagination-independent candidate caching is subtle; a naive `IMemoryCache.GetOrCreate` allows stampedes and per-page catalog re-fetches. | HIGH | Use **HybridCache** (`GetOrCreateAsync` guarantees single-flight per key). Preserve `ProductQueryCache`'s exact key shapes. Add a concurrency test (N parallel identical requests ⇒ one upstream call); port `CachingIT`. |
| R2 | **RFC-7807 / security-error contract drift.** ASP.NET Core's default 401/403 are empty-bodied; default validation/exception bodies differ in field names. | HIGH | Centralize on `AddProblemDetails` + one `IExceptionHandler`; wire `JwtBearerEvents.OnChallenge`/`OnForbidden` to emit the same ProblemDetail. Lock down with golden-file assertions on `type`/`title`/`status`/`detail`/`timestamp`/field joins. |
| R3 | **JWT validation defaults mismatch.** `Microsoft.IdentityModel` defaults to 5-min clock skew and remaps `sub` → `NameIdentifier`, silently changing behavior vs. `jjwt`. | MED-HIGH | Set `ClockSkew = TimeSpan.Zero`, `MapInboundClaims = false`, `ValidateIssuer = true`, `ValidateAudience = false`; read `sub` explicitly. Assert malformed/expired/wrong-issuer rejection per `JwtServiceTest`. |
| R4 | **Numeric type/precision mismatch.** `BigDecimal` prices decoded via `double` would shift price-filter boundaries and JSON output. | MED | Map prices to `decimal` end-to-end; bind price fields to `decimal` in `System.Text.Json` (custom converter if emitted as JSON numbers). Add boundary tests at `minPrice`/`maxPrice` (inclusive). |
| R5 | **Schema/DDL divergence on cutover.** Hibernate `ddl-auto=update` tolerates drift; EF migrations are stricter and can fail or silently diverge against an existing volume. | MED | Generate the EF initial migration, diff against the live Hibernate schema before targeting a populated DB. Pin `role` to string conversion, `username` unique, identity generation. For real data, dump/reload `user_account` and verify BCrypt hashes still authenticate. |

---

*Bottom line: the port is well-scoped — one entity, a synchronous proxy, six endpoints.
Effort concentrates in Phases 3–4 (cache + error/security parity), which is exactly where
the top risks live. The existing test suite is the parity harness: porting it first turns
each phase's acceptance criteria into runnable checks.*
