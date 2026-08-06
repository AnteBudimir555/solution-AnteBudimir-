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
>
> **Current state.** Phases 1–5 are complete: the port is behaviorally at parity (79/80 shadow
> cases, 0 unexpected contract differences, 133 tests passing on `net10.0`). **Phase 6 —
> Pre-Release Hardening — is open**, and holds four release blockers found by a pre-release review
> that asked whether the *matched* behaviour is fit to ship. Parity is not the same gate as
> readiness, and the shadow harness is structurally unable to raise most of Phase 6 because both
> services agree. Read §6.4 (Subtle traps) before implementing anything in §6.1–6.3.

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
> from the Java `src/` tree during migration). Phases 1-5 were built against `net8.0`,
> the plan's sanctioned fallback, because that was the only SDK installed at the time;
> the solution now targets **`net10.0`** as §2.1 specifies — see the runtime-bump note
> after Phase 5. The JWT fail-fast (`ValidateOnStart`) is deferred to
> Phase 4, where the Api DI container is wired; the `JwtOptions` validation attributes
> are already in place.

### Phase 1: Solution Setup & Shared Contracts
- [x] Create the .NET solution (`Abysalto.Middleware.sln`) and the project folders per §2.2 (under `dotnet/`).
- [x] Scaffold projects: `Middleware.Api` (webapi/minimal), `Middleware.Core` (classlib), `Middleware.Infrastructure` (classlib), `Middleware.UnitTests` + `Middleware.IntegrationTests` (xunit).
- [x] Wire project references: `Api → Infrastructure → Core`; test projects reference their targets.
- [x] Add root `Directory.Build.props`: `TargetFramework` (net8.0 initially, now net10.0), `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latest`.
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
- [x] Startup fails fast when the JWT secret is missing/too short. *(Done in Phase 4: `AddSecurityServices` binds `JwtOptions` with `ValidateOnStart`; verified by `StartupFailsFastWhenTheJwtSecretIsMissingOrTooShort`.)*
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
- [x] Apply migrations on startup via `InitializeDatabaseAsync` (Migrate for Npgsql / EnsureCreated for SQLite). *(Phase 4 hosts it as `DatabaseInitializer`, registered by `AddPersistence` so it always starts before the user seeder.)*
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
- [x] Wire Serilog (logging), the ProblemDetails handler (error handling), and shared utilities. *(Done in Phase 4, where the Api host and its middleware pipeline are wired.)*

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
- [x] Map the six endpoints in route groups (`/api/products` list/`{id}`/`filter`/`search`/`categories`, `/api/auth/login`).
- [x] Add FluentValidation rules: `page ∈ [0,10000]`, `size ∈ [1,100]`, free-text ≤ 100, `q` not blank, `id > 0`, cross-field `minPrice ≤ maxPrice` (→ `InvalidRequestException`).
- [x] Implement `JwtService` (issue/validate HS256): subject = username, issuer set + enforced, `expiresInSeconds` on the response; silent rejection of malformed/expired/wrong-issuer tokens.
- [x] Configure `JwtBearer`: `ClockSkew = TimeSpan.Zero`, `MapInboundClaims = false`, `ValidateIssuer = true`, `ValidateAudience = false`, symmetric HS256 key from config.
- [x] Implement `AuthEndpoints.Login`: BCrypt verify (`BCrypt.Net-Next`), issue token, log identity only (never password/token).
- [x] Implement `UserSeeder` as `IHostedService` (seed only when enabled and absent; store BCrypt hash only).
- [x] Configure authorization: public = auth + OpenAPI paths; everything else requires a valid bearer token.
- [x] Implement `ProblemDetailsExceptionHandler` (`IExceptionHandler` + `AddProblemDetails`): `type` URIs, `title`, `status`, `detail`, `timestamp`, field-level validation joins.
- [x] Wire `JwtBearerEvents.OnChallenge`/`OnForbidden` so 401/403 render the **same** ProblemDetail.
- [x] Implement `CorrelationIdMiddleware`: read/echo `X-Correlation-Id`, reuse inbound only if matching `[A-Za-z0-9._-]{1,64}`, push to Serilog `LogContext`, emit one completion line (method/path/status/duration), clear context afterward.
- [x] Configure Serilog sinks: compact JSON (Postgres) + readable console (Development); never log the Authorization header or login payload.
- [x] Configure CORS policy (explicit, even if permissive-for-dev) and request/response validation wiring.
- [x] Add the OpenAPI document + UI; mark `/api/auth/login` as public (no lock). *(Swashbuckle rather than Scalar — see the status note.)*
- [x] Invoke `InitializeDatabaseAsync` from the host (deferred from Phase 2) and wire options `ValidateOnStart()` + the JWT fail-fast (deferred from Phase 1).

> **Status — Phase 4 complete (API, auth, middleware).** Full suite: **120 passed, 0 skipped**
> (66 unit + 54 integration); `dotnet build -c Release` is warning-free under
> `TreatWarningsAsErrors`. The Testcontainers-Postgres migration test that Phases 2–3 reported
> as skipped now runs for real — Docker is available on this machine.
>
> Notes on the three places the .NET stack forced a decision:
> * **OpenAPI UI.** Swashbuckle generates both the document (`/swagger/v1/swagger.json`) and the UI
>   (`/swagger`) in place of Scalar. Originally forced: on the `net8.0` fallback
>   `Microsoft.AspNetCore.OpenApi` only contributes endpoint metadata, its document generator having
>   arrived in .NET 9. The net10 bump removes that constraint, but the swap was deliberately left out
>   of it — see the runtime-bump note. Either way it is a package choice, not a contract change: `OpenApiDocumentTests` pins the six paths, the single
>   `bearerAuth` scheme, per-operation security (login carries no lock), the shared 400/401/500 +
>   502/404 ProblemDetail responses, and the real parameter types.
> * **Parameter binding.** Endpoint parameters bind as `string?` and convert in `QueryParsing`.
>   Minimal-API binding failures produce an empty-bodied 400, whereas Spring reports a
>   `MethodArgumentTypeMismatchException` as a ProblemDetail naming the parameter; converting
>   explicitly keeps that contract. An operation filter restores the real types in the document.
> * **Unmatched paths.** An unknown path answers **404** (with the API's problem body), where the
>   Java service answers 401 because its security filter chain runs ahead of route resolution.
>   Reproducing that needs a catch-all route, which makes every method mismatch on a known path a
>   404 instead of a 405. Standard HTTP semantics won; this is the only intentional status
>   divergence in the phase.
>
> `ProblemBody` is serialized instead of ASP.NET Core's `ProblemDetails` so the member order, the
> custom `timestamp`, and the *absence* of framework extensions (`traceId`, `errors`) match Spring's
> `ProblemDetail` byte for byte. 403 is wired through `JwtBearerEvents.OnForbidden` and renders the
> same body, but no endpoint requires a role today, so — as in the Java service — nothing can
> currently produce one.

**Acceptance Criteria**
- [x] All endpoints enforce validation with the same status/message shape as the Java service. *(Hibernate-Validator messages restated verbatim; join shape asserted per parameter and per body in `ProblemContractTests`.)*
- [x] A seeded user authenticates; token `sub`/`iss`/`exp` match the Java claims; tampered/expired/wrong-issuer tokens are rejected. *(`SeededUserCanAuthenticateAgainstTheFreshlyCreatedSchema`, `JwtServiceTests` incl. the exact claim-set assertion.)*
- [x] 400 / 401 / 404 / 502 / 500 all produce byte-comparable ProblemDetail bodies. *(`ProblemContractTests` asserts the member set, `type`, `status` and `detail` for each. 403 shares the renderer but has no reachable trigger — see the status note.)*
- [x] Every log line carries the correlation id; the response echoes `X-Correlation-Id`; no credential material is logged. *(`CorrelationIdTests`.)*
- [x] `/swagger` renders; `/api/auth/login` shows no auth requirement. *(`OpenApiDocumentTests`.)*

### Phase 5: Testing & Feature Parity Verification
- [x] Implement automated unit tests — full xUnit ports of every existing unit test (services, mapper, cache, JWT, `TextUtils`, `CacheKeys`, `PagedResponse`).
- [x] Implement integration tests with `WebApplicationFactory<Program>` + WireMock.Net (upstream) + Testcontainers-Postgres (DB); port `ProductApiIT` and `CachingIT`.
- [x] Execute endpoint comparison tests: API-shadowing harness replays a fixed corpus against both Java and .NET services (same DummyJSON) and diffs normalized responses.
- [x] Snapshot both OpenAPI documents and diff paths/schemas to prove the contract is unchanged.
- [x] Package: multi-stage `Dockerfile` (`dotnet publish` → `aspnet:10.0`, non-root user); add `docker-compose.dotnet.yml` (keep `postgres:17`, map env vars, preserve the missing-secret fail-fast).
- [x] Final performance/load test against the new stack; compare latency and upstream-call counts to the Java baseline.

> **Status — Phase 5 complete (verification, packaging, parity).** Full suite: **133 passed, 0 skipped**
> (66 unit + 67 integration); `dotnet build -c Release` warning-free under `TreatWarningsAsErrors`.
>
> **The parity harness** lives in `dotnet/tools/Middleware.ShadowHarness` and has three modes, each
> writing a markdown report and exiting non-zero on an unexplained difference, so any of them can gate
> a pipeline:
> * `--mode shadow` replays **80 named cases** — every endpoint crossed with edge pages, filter
>   combinations, invalid parameters, auth states and routing near-misses — against both services
>   pointed at the same live DummyJSON, diffing status, content type and canonicalized JSON. Only
>   `token` and `timestamp` are masked; number formatting is compared as written, because a client
>   diffing payloads would see it.
> * `--mode openapi` flattens both documents into the statements a consumer can observe and diffs the
>   sets. A textual diff of two generators' output would be all noise.
> * `--mode load` puts a counting reverse proxy in front of each service, making upstream traffic a
>   measured number rather than an inference from latency.
>
> **The first shadow run reported 23 divergences**, none of which the ported test suite had caught —
> the Java tests never asserted them either, having inherited the behaviours from Spring. All but one
> are fixed:
> * Validation `detail` now carries Spring's `<controllerMethod>.<parameter>` property path
>   (`list.page: must be greater than or equal to 0`), which its `ConstraintViolationException` joins
>   into the message. 14 cases.
> * Whole-number doubles serialize as `4.0`, not `4` — Jackson renders every `Double` via
>   `Double.toString` (`JavaDoubleConverter`).
> * Framework 404/405 bodies restate Spring's `detail` verbatim, including the odd
>   `No static resource ...` wording, which is contract rather than description.
> * A trailing slash on `/api/` is a 404 instead of matching the route, and a method mismatch on the
>   public auth path answers 405 instead of being claimed by the fallback authorization policy and
>   challenged (`RoutingParity`). Both are cases where Spring resolves by path and ASP.NET Core by
>   endpoint — invisible on a matched request, visible on a near-miss.
>
> The **one remaining divergence** is the order multiple constraint violations are joined in. Spring
> takes them from Hibernate Validator's unordered violation set, which across the endpoints comes out
> as size/page, page/size and size/q — neither declaration nor alphabetical order. Reproducing it means
> emulating Java's hash layout, so the .NET side joins in declaration order; the case stays in the
> corpus with that reason recorded and is reported without failing the run. Every single-violation
> request — all the others — matches exactly.
>
> **The first OpenAPI diff reported 124 differences**, from six causes, all closed: component names
> (which decide what a generated client calls its classes), missing validation bounds (springdoc reads
> them off the Bean Validation annotations; here they must be stated), `q` documented as optional
> though omitting it is an error, the login body's `@NotBlank` constraints and required flag, `price`
> carrying `format: double` (which would tell a generator to bind `decimal` to a binary float), and
> absent field descriptions. One was a bug in the differ itself. **Two are accepted with reasons:**
> springdoc types success responses as `*/*` because the controllers declare no `produces`, and
> Spring's `ProblemDetail` hides the `timestamp` extension in an untyped `properties` map — matching
> either would mean publishing something the service does not do, and the shadow run proves the actual
> responses and error bodies are identical.
>
> **Packaging** adds `dotnet/Dockerfile` and `docker-compose.dotnet.yml` **alongside** the Java ones
> rather than replacing them, with its own project name, volume and host ports (8081, 5433), reading
> the same `.env` — so both stacks run at once, which is what lets the harness shadow them. Verified
> end to end: the stack comes up healthy, EF migrates a `user_account` matching the Hibernate schema,
> the seeded user authenticates, the container runs as uid 1654, and the missing-secret fail-fast holds
> at both levels (Compose refuses to interpolate an unset `JWT_SECRET`; `ValidateOnStart` rejects a
> short one). Shadowing the **packaged image** then found a real defect the local run could not:
> Minimal APIs only throw on an unreadable request body in Development, so a malformed body answered
> `Bad Request` in the container where dev — and Java — answer `Failed to read request`.
> `ThrowOnBadRequest` is now set explicitly.
>
> **Load** at concurrency 50, both services local, same origin behind the counting proxies:
>
> | Phase | Upstream calls (java / dotnet) | p50 (java / dotnet) | p95 (java / dotnet) |
> |---|---|---|---|
> | Cold single-flight (50 identical, uncached) | **1 / 1** | 1133 ms / 415 ms | 1134 ms / 415 ms |
> | Warm (500 requests, cached) | **0 / 0** | 26.0 ms / 11.9 ms | 86.7 ms / 31.0 ms |
> | Mixed corpus (500 requests) | **253 / 253** | 154 ms / 149 ms | 606 ms / 718 ms |
>
> The mixed-corpus 253 decomposes exactly on both sides: 250 uncached (list, by-id and categories go
> straight to the source in both services) plus 3 cached — the three price-filter pages sharing a
> single catalog fetch between them, which is the pagination-independent candidate caching (R1)
> working end to end. The cold-phase latency gap is JVM warm-up on the first upstream call, not steady
> state; the warm phase is the meaningful latency comparison, and the .NET service is faster there.

**Acceptance Criteria**
- [x] Full unit + integration suite green in CI. *(133 passed, 0 skipped — Docker is available, so the Testcontainers-Postgres test runs for real.)*
- [x] The shadowing harness reports zero response diffs across the corpus (status + normalized JSON) for all endpoints, edge pages, filter combinations, and error cases. *(79/80 identical; the one exception is the multi-violation join order, recorded with its reason — see the status note.)*
- [x] OpenAPI diff shows no path/schema changes. *(0 unexpected contract differences and 0 documentation differences; 2 accepted with reasons.)*
- [x] `docker compose up --build` brings up DB + .NET app; smoke suite passes. *(Smoke is the full shadowing corpus replayed against the packaged image: parity-clean.)*
- [x] Load test shows no material latency regression and cache single-flight holds under concurrency. *(Single-flight exact on both; identical upstream counts; .NET faster on the warm path.)*

### Runtime bump: `net8.0` → `net10.0`

Phases 1–5 were built against the plan's sanctioned .NET 8 fallback, that being the only SDK
installed. The solution now targets **`net10.0`** (§2.1), on SDK 10.0.302 / runtime 10.0.10.

- `TargetFramework` lives in `dotnet/Directory.Build.props` alone; the five per-project restatements
  of it (and of `Nullable`/`ImplicitUsings`) were removed rather than edited, so there is one source
  of truth to change next time.
- Framework-versioned packages moved to 10.x (EF Core, Npgsql, JwtBearer, Mvc.Testing, the
  `Extensions.*` family, Serilog.AspNetCore). Independently-versioned ones (xunit, FluentValidation,
  BCrypt, WireMock, Testcontainers) were left alone.
- **Two real vulnerabilities surfaced**, because the .NET 10 SDK audits transitive packages by
  default and `TreatWarningsAsErrors` promotes NU1903 to an error: `Microsoft.OpenApi` 2.0.0
  (GHSA-v5pm-xwqc-g5wc) and `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (GHSA-2m69-gcr7-jv3q). Both are now
  pinned forward by direct references, each carrying a comment saying why a package the code never
  calls is listed.
- Swashbuckle moved 6.9.0 → 10.2.3: ASP.NET Core 10 resolves Microsoft.OpenApi **2.x**, which is
  binary-incompatible with the 1.x that Swashbuckle 6 was built against. That forced a contained
  migration of `ApiDocumentationExtensions` to the 2.x model — root namespace, `JsonSchemaType`
  instead of type strings, `JsonNode` instead of `IOpenApiAny`, string-typed numeric bounds, and
  schemas/parameters exposed as interfaces that only a concrete instance can mutate. One trap worth
  naming: an `OpenApiSecuritySchemeReference` built without its host document resolves to no name and
  serializes as an empty `{}` — which reads as *no authentication required*. `OpenApiDocumentTests`
  caught it.
- **The Scalar swap was deliberately not folded in.** .NET 10 does ship the in-box document
  generator the plan sketches, so the Phase 4 deviation is now removable — but it would rewrite the
  operation and schema filters that pin the document to the Java contract, which is a contract
  change wearing a package change's clothes. It is worth doing on its own, against the OpenAPI diff.

**Parity re-verified on net10, all against the packaged image:** 133 tests pass (66 unit + 67
integration), the Release build is warning-free, the shadowing corpus is **79/80 identical** with the
same single known divergence, and the OpenAPI diff reports **0 unexpected** contract differences and
**0** documentation differences — the same numbers as on net8. Load behaviour is unchanged where it
counts: single-flight 1/1 upstream calls, warm 0/0, mixed corpus 253/253, with .NET still roughly 4×
faster than Java on the warm-path p50. (The absolute latencies are not comparable to the net8 run —
that run had a quieter machine — and no controlled net8-vs-net10 benchmark was done.)

### Repository restructure: the Java service removed

The migration having landed and been verified, the repository is now .NET only.

- **Deleted:** the entire `src/main/java` tree (63 files), `pom.xml`, the Maven wrapper (`mvnw`,
  `mvnw.cmd`, `.mvn/`), the Java `Dockerfile` and `docker-compose.yml`, the Java-oriented `.gitignore`
  / `.dockerignore` / `.gitattributes`, and Spring Boot's generated `HELP.md`.
- **Flattened:** `dotnet/src` → `src`, `dotnet/tests` → `tests`, and the solution,
  `Directory.Build.props`, `Dockerfile`, `.config`, `.gitignore` and `.dockerignore` up to the root.
  `docker-compose.dotnet.yml` becomes `docker-compose.yml`. **Every `dotnet/…` path in Phases 1–5
  above is therefore historical** — those statements were true when written and are left as the record
  of what happened, not corrected into a fiction about where the files are now.
- **Compose reverted to standard ports and names** (8080, 5432, project `abysalto-middleware`, volume
  `pgdata`). The odd 8081/5433 existed solely so both stacks could run at once for shadowing. Note the
  volume rename orphans any existing `pgdata-dotnet` volume — dev data only, but it is not migrated.

> **The parity harness was deleted with it, and that is a real loss worth stating plainly.** All three
> modes fetched from a running Java service and diffed against it (`OpenApiDiff.cs`, `LoadTest.cs`), so
> none of them can run without one; keeping ~1,000 lines that cannot execute would have been worse.
> But it means **the 80-case shadow corpus no longer guards anything**, and two Phase 6 traps (T5, T8)
> lose the gate they were written around. What remains is the ported test suite: 136 tests, including
> the RFC-7807 contract assertions and the OpenAPI document tests. Those are now the contract gate.
> Git history retains the harness if parity ever needs re-checking against the original.

Verified after the move: `dotnet build -c Release` warning-free, **136 passed / 0 failed / 0 skipped**
— the same numbers as before it, from the new layout.

### Phase 6: Pre-Release Hardening — **OPEN**

> **Status: §6.1 and §6.2 are closed.** The cache cluster (B2, B3, S2, S5), the SDK pin (S4), the
> .NET-only restructure that carried B4 with it, the seed-credential fix (B1), the upstream resilience
> pipeline (S1), the caching coverage gap (S3), the 401 challenge detail (S6) and the CORS allowlist
> (S7) have landed, and §6.3 has L1, L2 and L3 closed. Remaining: **L4 only**.
>
> **Verified on SDK 10.0.302** — the version the pin records and the Phase 5 parity run used,
> installed user-local to `~/.dotnet` after S4 landed. `dotnet build -c Release` is warning-free under
> `TreatWarningsAsErrors`, and the suite is **183 passed, 0 failed, 0 skipped** (82 unit + 101
> integration), up from the 139 baseline by the seven tests S1 adds, the six S3 adds, the five S6
> adds, the sixteen S7 adds, the six L1 adds and the four L2 adds.
>
> **The Phase 6 acceptance criteria below are stale for the cache cluster.** Three of them (bounded
> cache memory, bounded distinct fetches, the T1 invariant) are still `[ ]` although B2/B3/S2/S5 closed
> them with tests. Left as-is here rather than folded into an unrelated commit; it wants its own pass
> over §6 that reconciles the criteria against what actually landed.
>
> `AFullSizedPageIsRetainedUnderTheConfiguredSizeBound` passes against the real host, which is the
> point that matters for B3: the byte budget does retain a realistic 100-product page, so the unit
> error described in T2 is closed by a test rather than by an argument.
>
> **Still not run: the parity harness.** `--mode shadow` and `--mode load` need both services up
> against live DummyJSON. Load mode is expected to *fail* on B2 — see T5 — because .NET now makes
> fewer upstream calls than Java. That expectation must be updated in the same commit that runs it,
> not relaxed to make it pass.
>
> Several traps were settled empirically rather than by reasoning, and two of them were recorded
> wrongly in this document — see T1, T2, T3 and T4, all corrected in place with the measurements.
> T3 is the second prediction to be overturned by a probe: the framework had already handled the
> thing the note warned about, and the *real* hazard turned out to be a registration-ordering one the
> note did not describe.
>
> Phases 1–5 proved *parity*: the .NET service behaves as the Java one does.
> A pre-release review (staff-level, security/performance/architecture) accepted that parity and then
> asked a different question — whether the behaviour being matched is fit to ship. Most of it is. The
> items below are where it is not.
>
> **Three of these are inherited, not introduced.** Faithfully porting a design also ports its gaps,
> and the shadow harness cannot flag them precisely *because* both services agree. They are recorded
> here as deliberate divergences to take, not as porting defects. Each such item is marked
> **[inherited]**, and taking it will move a shadow-corpus or load-mode number — see §6.4.

#### 6.1 Release blockers

- [x] **B1 — Remove the known-credential account from the Production compose stack.** *(Done — at
  both layers, and it took `.env.example` with it.)*
  `docker-compose.yml` set `ASPNETCORE_ENVIRONMENT: Production` *and*
  `Security__SeedUser__Enabled: "true"` *and* `SEED_USER_PASSWORD:-demo1234`. The documented
  one-command startup therefore published a production-mode service with `demo`/`demo1234`,
  contradicting the stated intent in `appsettings.json` — *"Disabled by default so production-like
  configurations never create a known-credential account."*

  **Fixed in two independent places, so neither relies on the other.**

  1. *Compose.* `SEED_USER_PASSWORD` now uses the `${VAR:?message}` form `JWT_SECRET` already had.
     The username keeps its `:-demo` default deliberately — it is not a secret, and the account is
     only reachable by someone who already has the password, so requiring it adds friction rather
     than safety.
  2. *The application.* `SeedUserOptions` implements `IValidatableObject` and is registered with
     `ValidateDataAnnotations().ValidateOnStart()`: seeding enabled with a blank username or
     password now aborts start-up. This closes the path Compose cannot see — `docker run` with
     `Security__SeedUser__Enabled=true` and nothing else, which previously created an account whose
     password was the empty string.

  > **Blankness is the only judgement the application can honestly make**, and the plan's
  > "consider a startup guard" is worth recording as *rejected in the form it was proposed*. Whether
  > a password is *known* — because it came from a committed file — is invisible from inside the
  > process. An environment-gated guard (`!env.IsDevelopment()` ⇒ refuse) is worse than it looks: it
  > is defeated by setting `ASPNETCORE_ENVIRONMENT=Development`, which an operator careless enough
  > to ship `demo1234` will happily do, and it walks straight into T9. Provenance is knowable at the
  > Compose boundary, so that is where it is enforced.

  **T10 handled in the same change, and it went further than the plan expected.** `.env.example` now
  declares `SEED_USER_PASSWORD` with **no value**, so `cp .env.example .env && docker compose up`
  stops with a readable error instead of silently starting on a published credential. `JWT_SECRET`
  got the same treatment, which the plan had not called for: it shipped a *working* committed
  signing key, and whoever holds that can mint a valid token for any user and any role — strictly
  worse than one known account. `DB_USERNAME`/`DB_PASSWORD` keep throwaway values, and the file says
  why: the database is reachable only over the internal Compose network and a loopback-bound host
  port, and the volume is disposable.

  **The documented startup is now three commands, not one.** That is a real cost, paid knowingly:
  the alternative is a stack whose credentials are in the repository. The README leads with the
  `openssl rand` lines so nobody discovers the requirement by hitting the error.

  Verified: `docker compose config` on a verbatim copy of `.env.example` exits 1 naming
  `SEED_USER_PASSWORD`; filling only that one exits 1 naming `JWT_SECRET`; filling both resolves
  with `Security__SeedUser__Password` set to the supplied value. `StartupFailsFastWhenSeedingIsEnabledWithoutCredentials`
  covers the in-process half over blank, whitespace-only and blank-username cases.
- [x] **B2 — Bound the price-filter cache key space.** *(Done — by a different route than proposed.)*
  `CacheKeys.FilterCandidates` keyed on the raw `decimal` bounds. `Price()` collapsed `10` and
  `10.00`, but nothing collapsed `10.01` and `10.02`. Each distinct pair missed the cache, triggered
  `source.ListAsync(0, IProductSource.All, ct)` — a **full-catalog fetch** — and retained a full
  catalog copy in L1 for the TTL. One cheap authenticated request amplified into one full upstream
  download plus one full catalog retained, and single-flight could not help because the keys differed
  by construction.

  > **Implemented by removing the bounds from the key, not by quantizing them.** The plan proposed
  > rounding to cents and capping magnitude. That shrinks the key space without bounding it, needs a
  > new upper-bound validation rule (a wire-contract change the Java service does not have), changes
  > results at the rounding margin, and carries T1. The observation that retires all of it: **the
  > upstream call never depended on the price bounds at all** — `FetchPriceFilteredAsync` fetched the
  > same catalog whatever they were, then filtered in memory. A value that does not change the
  > fetch has no business in the key. The cache now holds the *unfiltered* candidate set keyed by
  > category alone, and the bounds are applied per call over it. Key space: one entry per category.
  > Upstream fetches for any number of distinct ranges over one category: one. No new validation, no
  > contract change, no rounding, and T1 cannot occur.

  Proven by `VaryingPriceBoundsShareOneUpstreamFetch` (200 distinct bounds → 1 upstream call) and
  `PriceBoundsStillFilterTheCachedCandidateSetPerCall` (one shared fetch must not become one shared
  answer). Both pass.
- [x] **B3 — Implement or delete `Cache:MaximumSize`.** *(Done — implemented, with corrected units.)*
  `CacheOptions` bound it, documented it as *"Bounds the size … the .NET analog of
  `maximumSize=500,expireAfterWrite=60s`"*, and never read it — `grep -rn "MaximumSize" src/` returned
  only the declaration. The backing `IMemoryCache` had no `SizeLimit`, so the cache was bounded by the
  60s TTL alone. Now `AddMemoryCache(o => o.SizeLimit = …)` is registered ahead of `AddHybridCache`
  (which only adds one if absent), plus `MaximumPayloadBytes`/`MaximumKeyLength`.

  > **The option was renamed to `MaximumSizeBytes`, because the unit changed and silence was the
  > failure mode.** See T2: the budget is bytes, not entries, and `500` carried across verbatim
  > retains nothing while throwing and logging nothing. `MaximumEntryBytes` caps a single entry so one
  > pathological response cannot evict everything else. A deliberate, documented divergence from the
  > Caffeine spec — equivalent capacity, not an equivalent number.
- [x] **B4 — Document the .NET service.** *(Done, as part of the repository restructure below —
  removing the Java service made the old README describe nothing that still existed.)*
  `grep -in "dotnet\|\.NET" README.md` used to return **zero** matches: the README described the Java
  service only (JDK 21+, `source/ProductSource.java`). It is now rewritten for this service —
  prerequisites and the SDK pin, run/test commands for all three paths, the Compose stack and its
  configuration keys, the endpoint table, the auth and error contracts, the caching and push-down
  policies, and the project layout.

  > Two things it stated rather than hid, because a README that omits them is worse than none: the
  > Compose stack's then-open B1 problem carried a warning against exposing it, and the endpoints
  > that are *not* cached (S3) are named as such. Both have since been overtaken by their fixes: the
  > B1 warning is replaced by the corrected instructions, and the S3 note is replaced by a table of
  > what is cached, with which TTL, and the one endpoint that is deliberately not.

#### 6.2 Should-fix before release

- [x] **S1 — Add a resilience pipeline to the upstream client.** *(Done — and the trap it was
  guarded against turned out not to exist on this version; see T3.)*
  `UpstreamServiceCollectionExtensions` registered a typed client with a base address, timeouts and
  decompression, and nothing else; `Microsoft.Extensions.Http.Resilience` was not referenced. A single
  DummyJSON blip failed every in-flight request with no retry, and a sustained outage had every
  request burn the full 5s response budget with no breaker to shed load. Now
  `.AddStandardResilienceHandler()` on the same registration, plus `PooledConnectionLifetime` for DNS
  rotation.

  > **The budget was split rather than extended, which is a departure from the numbers T3 proposed.**
  > T3 suggested attempt = `ResponseTimeoutMs` and total = `3 ×` it. Measured, that combination costs
  > the *client*: with the library's stock 2s exponential backoff, a hard-down upstream took
  > **7937 ms** to surface a 502 where it previously took ~5s at worst, and the proposed total would
  > let it reach 15s. Instead `ResponseTimeoutMs` (5s, unchanged) is now the **total** budget and a
  > new `AttemptTimeoutMs` (2s) bounds one attempt, with retries cut to 2 at a 200 ms base — the same
  > failure surfaces in ~306 ms in the tuned shape. **Adding resilience did not make a dead upstream
  > slower to report.** The cost, paid knowingly: an upstream that is *slow* rather than failing now
  > gets roughly two and a half attempts inside the 5s rather than one attempt using all of it.

  > **The stock circuit-breaker settings would have been decorative.** Defaults are
  > `FailureRatio = 0.1` with `MinimumThroughput = 100` over 30s. Probed with retries disabled, the
  > breaker opened at request **#101** — a service at this traffic level never reaches that in a
  > window, so the breaker would have been configuration without behaviour. Shipped at `0.5` / `20`:
  > at least twenty attempts seen and at least half failing. The ratio is *raised* from the default
  > on purpose — for a proxy, the occasional upstream 5xx is weather, not an outage.

  **Eight new `Upstream:*` keys, which is the main thing to argue with in review.** They are what
  makes the pipeline testable — the breaker test sets `MinimumThroughput` to 4 rather than issuing a
  hundred requests — and tunable without a rebuild. `UpstreamOptions` implements `IValidatableObject`
  and is registered `ValidateOnStart()`, so `AttemptTimeoutMs ≥ ResponseTimeoutMs` or a too-short
  sampling duration aborts the host naming the key. Without that the pipeline still rejects the
  combination, but only when the typed client is first resolved — during a request, as a 500 that
  reads like a DI fault (T3's second half, confirmed).

  Covered by `UpstreamResilienceTests` (7 tests): transient blip retried to success, persistent
  failure retried to exactly the configured bound, 404 not retried, `HttpClient.Timeout` is
  `InfiniteTimeSpan`, the retry sequence outlasts one attempt, the breaker opens and then reaches the
  upstream zero further times, and an unreachable upstream still surfaces as `UpstreamException`.
  These are the first tests to exercise the **DI-wired** client at all: `MiddlewareApiFactory`
  substitutes `IProductSource` outright, and `DummyJsonProductSourceTests` news up a bare
  `HttpClient`, so the registration itself had no coverage.
- [x] **S2 — Mark the cached records immutable.** *(Done.)* `HybridCache` returns the stored instance
  from L1 only for types it can prove immutable; everything else is serialized on write and
  **deserialized on every read**. Measured on 10.8.0: two hits on one key return two *different*
  instances for a plain record, and the *same* instance once `[ImmutableObject(true)]` is applied — so
  a cache hit on a 100-product page was deserializing 100 products with nested reviews, every time.
  The attribute is now on `Product`, `ProductPage`, `Review`, `Meta` and `Dimensions`.

  > Two details the obvious version misses. **T4:** the attribute makes the promise load-bearing, so
  > `DummyProductMapper` now freezes every collection (`ImmutableArray.CreateRange`) — an
  > `IReadOnlyList<T>` over a `List<T>` is a view, and the mapper's `.ToList()` calls were producing
  > exactly that. **And the cached type has to be the marked one:** `PriceFilteredCandidatesAsync`
  > cached `IReadOnlyList<Product>`, an interface no attribute can vouch for, so it would have kept
  > deserializing. It now caches the `ProductPage` record instead.
- [x] **S3 — Close the caching coverage gap.** *(Done — all three parts, as proposed.)*
  *(partly [inherited])* `ProductService.ListAsync`, `GetByIdAsync` and `CategoriesAsync` called
  `source.*` directly; only filter and search routed through `IProductQueryCache`. That was faithful —
  `grep -rn "Cacheable" src/main/java/` returned three annotations, all in `ProductQueryCache.java` —
  but two consequences were worth fixing rather than inheriting:
  - `GET /api/products/categories` hit the upstream on **every** request, for data that changes
    approximately never. Now cached via a new `IProductQueryCache.CategoriesAsync` under its own
    `Cache:CategoriesExpireAfterWriteSeconds` (1h), separate from the general 60s TTL because the two
    age differently — a product page goes stale as stock and price move, a category list does not.
  - `GET /api/products?page=0&size=20` was uncached while `GET /api/products/filter?page=0&size=20`
    returned byte-identical data *from cache*. `ListAsync` now routes through
    `queries.CategoryPageAsync(null, page, size, ct)`, which issues the identical `source.ListAsync`
    call, so the two share one entry. **T5 is moot — the harness is gone.**
  - `GetByIdAsync` stays uncached, now with the reasoning written down on the method rather than
    looking like an oversight, and pinned by `GetByIdGoesStraightToTheSource` so the decision is
    visible if someone later routes it through the cache.

  > **The category list needed a wrapper record, and the intuitive choice would have been wrong.**
  > Per S2/T4 the cache returns the stored instance only for a type carrying
  > `[ImmutableObject(true)]`. Probed on Hybrid 10.8.0 with reference identity across two hits:
  > `IReadOnlyList<string>`, `string[]`, an unmarked record and — the one that looks safest —
  > **`ImmutableArray<string>`** all return a *different* instance per hit; only the marked record
  > returns the same one. Hence `CategoryList`, with its names frozen on the way in.

  **The real cost landed in the tests, not the code.** Caching three more endpoints made cache state a
  cross-test concern: `UpstreamFailureIsReportedAs502` and `UnexpectedFailureIsReportedAsAGeneric500`
  both began passing on a warm entry written by an earlier test, never consulting the substitute they
  had just configured to throw. Both suites now clear the cache in `InitializeAsync` alongside
  `ClearSubstitute`. Any future test that stubs a failure on a cached endpoint has the same
  requirement — that is the standing consequence of this item.

  > **`HybridCache` does have a clear-all, and it is `RemoveByTagAsync("*")`.** Measured: it evicts
  > entries written with **no tags at all**, so nothing in production had to be tagged for tests to
  > reset state. `CachingTests` therefore drops the hand-maintained key list it carried — which was
  > itself a trap, since a test touching a key nobody remembered to add would silently inherit the
  > previous test's entry. This retires the `CachingTests.cs` half of **L3**; the
  > `ApiDocumentationExtensions.cs` half is still open.
- [x] **S4 — Add `global.json`.** *(Done.)* `Directory.Build.props` targets `net10.0`; a machine with
  SDK 8.0.300 failed all six projects with `NETSDK1045` and no indication of what was required. The
  pin is at the **repository root** (T6: a `global.json` in `dotnet/` is invisible to `dotnet test`
  run from the root, which is where the harness is invoked; Maven ignores it). The failure now states
  its own fix:

  ```
  Requested SDK version: 10.0.302
  global.json file: C:\...\MiddlewareRestApi\global.json
  Install the [10.0.302] .NET SDK or update [...\global.json] to match an installed SDK.
  ```

  > `rollForward` is **`latestMinor`**, not the `latestFeature` this plan first suggested. Both fix the
  > default (`latestPatch`, which rejects a newer feature band), but the requirement here is "any .NET
  > 10 SDK, newest installed", and `latestFeature` is pinned to major.minor `10.0` — it would reject a
  > future 10.1 SDK for no reason. The version is a floor, kept at the 10.0.302 the parity run used, so
  > the pin still records what was verified.
- [x] **S5 — Enforce `MaxInMemoryCandidates` instead of narrating it.** *(Done.)* It logged a warning
  suggesting someone *"consider pushing the price filter down"*, then materialized and cached the full
  set anyway — a threshold that cannot stop anything is a log line, not a limit. It now logs at
  `Error` and throws `UpstreamException`, surfacing as a 502 rather than an unbounded allocation
  driven by upstream catalog size on a request a client can repeat. Covered by
  `PriceFilteredCandidatesRefusesToLoadMoreThanTheInMemoryThreshold`.
- [x] **S6 — Correct the 401 detail on the challenge path.** *(Done, as proposed.)*
  `ApiSecurityExtensions.cs:91` rendered `"Invalid username or password."` for *every* challenge —
  missing header, expired token, bad signature, deleted user. That wording is correct only for
  `POST /api/auth/login`. The enumeration concern that justifies a generic message applies to the login
  path; the challenge path leaks nothing by being accurate. It now renders
  `"Missing or invalid bearer token."`, and the login path is untouched — the two 401 details are now a
  deliberate pair, so the change is only safe as long as they stay distinct. Both sides carry a comment
  saying so, and a test pins each.

  > **The handler's claim to cover four causes was asserted, not assumed.** All four are now driven
  > end-to-end through the running host and must render one identical body: an unparseable token, a
  > `Bearer` header carrying nothing, a well-formed token signed with a different secret, an expired
  > token (mintable because `JwtService` writes `exp` explicitly and validates at zero clock skew), and
  > a valid token whose subject has since been deleted. The last is the one worth having: it fails in
  > `OnTokenValidated` via `context.Fail(...)`, not in token parsing, and reaching `OnChallenge` from
  > there is what lets a single wording cover the path at all. Measured: it does. Had it not, the
  > deleted-user case would have fallen through to the framework's empty-bodied 401 and broken the
  > RFC-7807 contract (R2) rather than merely being worded wrongly.

  **[inherited]** — the Java filter chain has the same wording, so this would have moved shadow cases.
  Moot: the harness is gone (T5), so nothing gates on the old string.
- [x] **S7 — Restrict CORS.** *(Done — and it uncovered two live bugs, neither of which this item
  predicted.)* `Program.cs:65-66` allowed any origin, header and method. It is now an allowlist bound
  from `Cors:AllowedOrigins`, **defaulting to empty** — no browser origin at all, which is the right
  default for an API whose callers are server-side. `CorsPolicyOptions` validates each entry on
  start-up, because every way of misspelling an origin is accepted by `WithOrigins` and then matches
  nothing, with no error and no log line. `AllowCredentials` is not set (T7), and
  `CorsTests.CredentialsAreNeverAllowed` is the replacement guardrail.

  > **The stated justification was wrong, and the tests say so.** This item claimed *"any origin can
  > drive the API with a stolen token"*. Restricting CORS does not address that. Measured against the
  > running host: a request from a disallowed origin still reaches the endpoint, still executes it,
  > and still returns 200 with the complete body — only the absent allow-header stops the calling
  > script from reading it, and a non-browser client ignores the mechanism entirely. A stolen token
  > still works from anywhere. What the change actually delivers is a bound on which *web pages* can
  > use the API from a visitor's browser. That is worth having, but it is not an access control, and
  > `ADisallowedOriginIsStillServedByTheEndpoint` asserts the limitation rather than leaving it to a
  > comment nobody re-reads.

  > **Two bugs found by writing the tests first, both invisible without them.**
  >
  > 1. **Preflight to `/api/auth/login` answered 405, so no browser could ever log in
  >    cross-origin.** `OPTIONS` matches no route there, so routing selects the framework's 405
  >    short-circuit — and `UsePublicPathMethodMismatch` exists precisely to *run* that short-circuit
  >    on public paths. It sat ahead of `UseCors`, so the preflight was refused before CORS saw it and
  >    the real `POST` was never sent. Fixed by moving `app.UseCors()` between `UseRouting()` and
  >    `UsePublicPathMethodMismatch()`. This was **already latent under `AllowAnyOrigin`** — the
  >    permissive policy did not save it — so it predates this item and would have shipped.
  > 2. **An eager `builder.Configuration.GetSection(...).Get<CorsPolicyOptions>()` silently read an
  >    empty allowlist.** Top-level statements run before the host is built, so that read sees only
  >    the sources registered by that point and ignores any added later. The policy is now built from
  >    `IOptions<CorsPolicyOptions>` via `AddOptions<CorsOptions>().Configure<TDep>(...)`, the same
  >    shape `AddApiSecurity` already uses for `JwtBearerOptions`. **Worth noting: `AddUpstreamSource`
  >    (S1) reads its options the same eager way.** It is not wrong there today — nothing overrides
  >    `Upstream:*` after builder construction, and the integration suite substitutes `IProductSource`
  >    outright so it never notices — but it is the same latent trap and is now recorded as such.
  >
  > A third measured behaviour needed no fix: `Vary: Origin` is emitted only when two or more origins
  > are configured. That is safe (with one origin the header is constant for every request that gets
  > one), but `ResponsesVaryByOriginWhenMoreThanOneIsAllowed` uses a two-origin fixture deliberately,
  > since reducing it to one would make the assertion silently vacuous.

  Cost paid knowingly: **this is a breaking change for any existing browser client.** The previous
  policy answered `*`; the new default answers nothing. A deployment that needs browser access must
  now name its origins or its client breaks with an opaque CORS error. That is the intent of the
  item, but it is a real migration step for an operator, and it fails closed rather than loudly.

#### 6.3 Lower priority

- [x] **L1 — Surrogate-pair split in `TextUtils.Truncate:33`.** *(Done — as proposed, with the
  symptom corrected.)* `text.Substring(0, budget)` cut at a UTF-16 index; an emoji straddling index 99
  yielded a lone surrogate. **[inherited]** — Java's `substring` splits identically, so this is a
  *parity-breaking correctness fix*. See T8.

  Fixed by stepping the window back one unit when — and only when — the cut would land between a
  high and a low surrogate, so a straddling character is dropped whole. The word-boundary threshold
  still divides the original `budget`, so **no input that does not straddle a pair changes at all**;
  the cost is one character of an already-truncated description, on the hard-cut path only. A
  boundary cut lands on a space, which is never half of a pair, so that path was never at risk —
  measured, not assumed.

  > **The plan said `System.Text.Json` "emits `U+FFFD`", and that is half the story.** Probed on
  > .NET 10 against a verbatim copy of the old method: the serializer does not emit a raw replacement
  > character, it writes the six-character **escape** (backslash, `u`, `FFFD`) into the JSON text.
  > So the defect is invisible to any test that greps the response body for `U+FFFD` — the bytes on
  > the wire are ASCII. It only
  > materializes when a client *deserializes*, which is why the regression test round-trips through
  > `JsonSerializer` rather than asserting on the serialized string. Nor does the serializer throw,
  > and `TrimEnd()` does not strip an orphaned surrogate: there is no point at which this failed
  > loudly.
  >
  > | input (`maxLength = 10`) | old result | new result |
  > |---|---|---|
  > | `abcdefgh😀ijklmnop` | `abcdefgh` + lone `D83D` + `…` → client reads `abcdefgh�…` | `abcdefgh…` |
  > | `abcdefg😀hijklmno` (pair fits) | `abcdefg😀…` | unchanged |
  > | `hello wo😀rld and more` (boundary path) | `hello…` | unchanged |

  > **Scope deliberately stopped at surrogate pairs, and the wider problem is real.** The same probe
  > split a `👨‍👩‍👧` ZWJ sequence and an `e`+combining-acute cluster. Both produce **well-formed
  > UTF-16** and survive JSON round-trip intact — they render as a different but valid string, not as
  > a replacement character. Truncating on grapheme clusters (`StringInfo`) would fix those too, but
  > it is a behaviour change on ordinary text with no correctness failure behind it, so it is not
  > folded in here.

  Six tests added to `TextUtilsTests`: the straddling drop, a pair that fits being kept (so the guard
  cannot over-trim), the JSON round-trip, the word-boundary path being unperturbed, `maxLength = 2`
  (where the budget is one unit and the result is the ellipsis alone — the index arithmetic's low
  end), and a lone surrogate **already present in the input** being passed through. That last one
  fixes the contract deliberately: truncation never *introduces* a lone surrogate, but it does not
  repair malformed input, which would be a lossier promise than the one this method should make.
- [x] **L2 — Document the case-insensitivity contract.** *(Done — on two methods, not one, and with a
  test the item did not ask for.)* `ProductQueryCache` passes the *normalized* (lower-cased, trimmed)
  query to `SearchByNameAsync`. That is what makes the cache key and the upstream call provably
  consistent — good — but it silently imposes case-insensitive search on every future source, and
  `IProductSource` did not say so. Now stated as a **free-text convention** on the interface, with the
  consequence spelled out rather than the mechanism: the caller's original casing is *gone* by the time
  a source sees the value, so a case-sensitive source answers `iPhone` with the results for `iphone`
  and no layer above can detect the substitution.

  > **The item was under-scoped: the same imposition applies to `FindByCategoryAsync`.**
  > `ProductQueryCache` normalizes the category at three call sites (`:64`, `:109`) and passes it
  > down at `:73` and `:121`, exactly as it does the query — so a source matching categories
  > case-sensitively fails the same way. Both methods now carry the constraint; documenting only
  > `SearchByNameAsync` would have left half the contract unstated. *(The line numbers in the original
  > item, `:39,45`, predate S3's `CategoriesAsync` addition.)*

  > **`ToLowerInvariant` turned out to be load-bearing, and nothing was pinning it.** Probed across
  > four locales: `"ISTANBUL".ToLower()` is **`"ıstanbul"`** under `tr-TR` and `az-Latn-AZ`, and
  > `"istanbul"` under `en-US` and `lt-LT`. Because this value is passed to the source *as well as*
  > into the key, swapping the overload would change **which upstream call is made** depending on the
  > host's locale — not merely which key it is filed under. `NormalizeTextLowercasesInvariantlyWhateverTheHostLocale`
  > now names the cultures explicitly. Verified by mutation, not by inspection: flipping the
  > implementation to `ToLower()` fails 2 of its 4 cases with `Expected "istanbul" / Actual "ıstanbul"`,
  > and passes the `en-US` case — which is precisely why a CI machine would never have raised it.

  > **One precision the contract states rather than glosses:** invariant lower-casing is *not* full
  > Unicode case folding, so it is not the `OrdinalIgnoreCase` relation. Measured: `Σ` and `ς` are
  > equal under `OrdinalIgnoreCase` but normalize differently; `İ` (U+0130) does not lower to `i`, and
  > `ß` does not fold to `ss`. A source writing its own comparer should therefore compare the value
  > **ordinally, as given**, not re-fold it.

  The behavioural half was already covered — `SearchInputsThatNormalizeToTheSameKeyShareOneUpstreamCall`
  (integration) and `SearchNormalizesQueryAndTranslatesOffset` / `CategoryPageWithCategoryQueriesThatCategory`
  (unit) pin that the normalized value is what reaches the source. Culture-invariance was the one part
  of the contract with no test behind it. Also confirmed against the **live** DummyJSON that the one
  real implementation honours the contract being stated: `q=phone|Phone|PHONE|pHoNe` all return
  `total=23`.
- [x] **L3 — Clear the stale post-bump comments.** *(Done — the S3 half, and now the second.)*
  ~~`ApiDocumentationExtensions.cs:15-19` still says *"On the net8.0 target … its document generator
  arrived in .NET 9"*.~~ **Done.** The claim was false on two counts: the target has been `net10.0`
  since the runtime bump, and the in-box generator it says is missing is present. Verified rather
  than assumed — a scratch `net10.0` web project referencing the same `Microsoft.AspNetCore.OpenApi`
  10.0.10 compiles `AddOpenApi()` + `MapOpenApi()` with 0 errors.

  > **The comment was contradicted by the file next to it.** `Middleware.Api.csproj:3-10` already
  > carried the corrected reasoning — .NET 10 ships the generator, the Scalar swap is deliberately
  > deferred because it would rewrite the filters that pin the document to the Java contract. So the
  > repository stated both the false version and its correction, a few lines apart, and a reader had
  > no way to tell which was current. The `.cs` comment now says what the `.csproj` says: Swashbuckle
  > is a **retained** choice, not a forced one, and what retains it is the operation and schema
  > filters in that same file.
  >
  > Two things checked and deliberately left alone. `Directory.Build.props:2-7` also mentions the
  > .NET 8 fallback, but as accurate *history* alongside a correct statement of the current target —
  > it is a record, not a stale claim, and the same reasoning that preserved the historical `dotnet/…`
  > paths in Phases 1–5 applies. `CachingTests.cs:25` mentions the old target too, in the past tense,
  > as the record of what S3 corrected. A repo-wide sweep for `net8`/`net9`/`.NET 8`/`.NET 9` and for
  > undated phrasings (*"arrived in"*, *"on this target"*, *"only contributes"*) outside `bin`/`obj`
  > found nothing else, so L3 is closed rather than merely advanced.

  ~~`CachingTests.cs:24-26` still says *"HybridCache on this
  target framework exposes no clear-all (tag-based eviction arrived in .NET 9)"* — it is available
  now, and the per-key eviction workaround is no longer needed.~~ **Done in S3**, which needed the
  clear-all: the claim was verified (`RemoveByTagAsync("*")` evicts even untagged entries) and the
  per-key list is gone. In a codebase where the comments carry this much of the reasoning, stale ones
  cost more than usual — this one had been standing in for a capability the tests actually needed.
- [ ] **L4 — Reconsider `IProductSource`'s shape.** The boundary itself is airtight: the `Dummy*` DTOs
  are `internal`, `DummyProductMapper` is the only type naming them, and `Core` does not reference
  `Infrastructure` — a leak is a compile error, not a review catch. Two shape issues remain:
  - `IProductSource.All = 0` encodes DummyJSON's own wire convention (`limit=0` means everything) into
    the abstraction, and it passes straight through to the URL. Every future source must honour *"give
    me the entire catalog in one call"* — precisely what a source large enough to need an abstraction
    cannot do.
  - Price range appears in none of the five signatures, so a source that filters on price natively
    (SQL, Elasticsearch) has **no way to say so** and will be handed `limit=All` and filtered in
    process memory. The abstraction is extensible for *swapping* sources but not for *capability*.

    A query object fixes both, and changes no consumer:
    ```csharp
    public sealed record ProductQuery(
        string? Category, string? NameContains,
        decimal? MinPrice, decimal? MaxPrice,
        int Skip, int Limit);

    public sealed record ProductQueryResult(ProductPage Page, bool PriceFilterApplied);

    Task<ProductQueryResult> QueryAsync(ProductQuery query, CancellationToken ct = default);
    ```
    `ProductService` then applies the in-memory price filter only when `PriceFilterApplied` is false —
    DummyJSON keeps today's behaviour, a capable source skips the full fetch entirely, and neither the
    endpoints nor the service change again.

#### 6.4 Subtle traps

Ordered by how quietly each one fails. **T1 is the only item here that can corrupt a response.**

- **T1 — Quantizing the price key without quantizing the filter serves one client another's results.**
  **Designed out rather than defended against — see the B2 note.** The trap was real for the
  quantization approach this plan originally proposed: rounding `minPrice` in the key while
  `MatchesPrice` still filtered on the unrounded value would make `10.011` and `10.014` share a key,
  and the second caller would receive the first caller's result set — silently, with a 200, only on
  colliding requests. Removing the bounds from the key entirely retires the whole class: the key
  depends on the category alone, the bounds are applied per call, and the stated invariant (*"two
  inputs share a key only when they also produce the same upstream call"*) holds trivially because the
  upstream call never depended on them. `PriceBoundsStillFilterTheCachedCandidateSetPerCall` pins the
  half that could still regress — that sharing one *fetch* must not become sharing one *answer*.
- **T2 — `SizeLimit` counts bytes, not entries, and a limit in the wrong unit silently disables the
  cache.** ~~Predicted: missing per-entry sizes make `MemoryCache` throw on every write.~~ **Measured,
  and the prediction was wrong in a more dangerous direction.** Probed against
  `Microsoft.Extensions.Caching.Hybrid` 10.8.0 (`SizeLimit` set, 200 keys, factory invocations
  counted):

  | `SizeLimit` | payload | retained |
  |---|---|---|
  | none | 512 B | 200/200 |
  | **500** | 512 B | **0/200** |
  | 100 000 | 512 B | 194/200 |
  | 1 000 000 | 512 B | 200/200 |

  HybridCache **does** size its L1 entries, so nothing throws and eviction works correctly — but the
  budget is **bytes**. Caffeine's `maximumSize=500` counts *entries*; carried across verbatim it means
  *500 bytes*, and the cache then retains nothing large enough to matter. No exception, no warning, no
  log line — every request simply becomes a miss. **This passes CI**, because the existing cache
  fixtures are empty `ProductPage`s small enough to fit under any limit, so the retention assertions
  stay green while production caches nothing. Hence `MaximumSizeBytes`/`MaximumEntryBytes` (named for
  their unit) and `AFullSizedPageIsRetainedUnderTheConfiguredSizeBound`, which uses a realistic
  100-product page precisely so the unit error cannot hide behind a small fixture.
- **T3 — `HttpClient.Timeout` and the retries added in S1.**
  ~~Predicted: `client.Timeout` applies to the whole pipeline including every retry attempt, so the
  current `Timeout = ResponseTimeoutMs (5s)` caps the standard handler's total budget at 5s and the
  retries never fire — a resilience handler that appears configured, passes startup validation, and
  does nothing. Fix by setting `client.Timeout = Timeout.InfiniteTimeSpan`.~~
  **Measured, and the prediction was wrong: `AddStandardResilienceHandler` already does this itself.**
  Probed against `Microsoft.Extensions.Http.Resilience` 10.8.0, reading `HttpClient.Timeout` back off
  the resolved client for the exact registration shape S1 touches
  (`AddHttpClient<IProductSource, DummyJsonProductSource>(c => c.Timeout = …)` +
  `ConfigurePrimaryHttpMessageHandler`):

  | registration | resolved `HttpClient.Timeout` |
  |---|---|
  | no resilience handler, `Timeout = 5000ms` | `00:00:05` |
  | `+ AddStandardResilienceHandler`, handler ordered either side of the primary handler | `-00:00:00.001` (`InfiniteTimeSpan`) |

  The handler overwrites the configure action's value, so the retries were never at risk. Confirmed
  behaviourally: against a 400 ms-delayed 500 with a 1s attempt timeout, **4 upstream hits in 1640 ms
  whether `client.Timeout` was 1s or infinite** — the sequence ran straight through the 1s.

  > **The trap is real but relocated: it is an ordering hazard, not a default.** A timeout applied
  > *after* the handler wins, and does exactly what the original note predicted:
  > ```
  > resilience, then ConfigureHttpClient(c => c.Timeout = 500ms)
  >     -> TaskCanceledException in 501ms, one attempt
  > ```
  > So S1 sets `client.Timeout = Timeout.InfiniteTimeSpan` anyway. It is belt-and-braces on 10.8.0,
  > and it is labelled as such in the code rather than dressed up as the fix — the point is to state
  > the intent and give `RetriesAreNotCancelledByAClientTimeout` an invariant to pin, so a future
  > `ConfigureHttpClient` in the wrong position fails a test instead of quietly disabling retry.

  **The second trap inside the first was right, and is worse than described.** The standard handler
  does validate these against each other (sampling ≥ 2× attempt, attempt < total) with clear messages
  — but *not at startup*. Options are resolved when the typed client is first created, which is
  during a request, so a misordered config is a 500 on the first call rather than a boot failure.
  S1 therefore validates the relationships on `UpstreamOptions` with `ValidateOnStart()`. Also
  discovered here: `Retry.MaxRetryAttempts = 0` is rejected outright
  (`must be between 1 and 2147483647`) — retry cannot be turned off that way, which matters for
  anyone trying to disable it in a test.

  A third finding worth carrying: the retry predicate is already correct for this service without
  configuration — `408`, `429` and `5xx` are retried, `400`/`404`/`409` are not. The
  `ProductNotFoundException` path is untouched by S1, and a test pins it.
- **T4 — `[ImmutableObject(true)]` promises deep immutability that `IReadOnlyList<T>` does not
  provide.** The attribute makes `HybridCache` hand the *same instance* to every concurrent caller.
  `Product.Tags` and `Product.Images` are `IReadOnlyList<string>`, and `DummyProductMapper` builds them
  with `.ToList()` — `IReadOnlyList<T>` is a read-only *view* over a mutable `List<T>`, not an
  immutable collection. Any consumer that casts back mutates the cached instance for every in-flight
  request. Nothing cast back at the time, so S2 would have been safe *as of that commit* — precisely
  the kind of safety that expires without warning. **Confirmed and addressed in S2:**
  `DummyProductMapper.Freeze` now returns `ImmutableArray.CreateRange(...)` for tags, images and
  reviews, and `ToPage` freezes `ProductPage.Items`, so the promise the attribute makes is
  structurally true rather than circumstantially true. Any new source must do the same — the contract
  is stated on `Product`.

  A second half of this trap only appeared during implementation: **the cached type must itself be the
  marked one.** `PriceFilteredCandidatesAsync` cached `IReadOnlyList<Product>` — an interface, which no
  attribute can vouch for and which the cache therefore keeps deserializing however immutable the
  *elements* are. Marking `Product` alone would have looked correct and changed nothing. It now caches
  the `ProductPage` record.
- **T5 — ~~S3 intentionally moves the load-mode numbers, and the harness gates on them.~~**
  **Obsolete: the harness was deleted with the Java service (see the restructure note above), so there
  is no longer a parity gate to move.** This trap mattered while both services ran side by side; it is
  kept for the record, and because its replacement question is now open — *nothing* currently catches
  a behavioural regression against the original contract except the ported test suite. The integration
  tests assert the RFC-7807 shapes and the OpenAPI document, which is the bulk of it, but the 80-case
  shadow corpus is gone. Treat the OpenAPI document tests as the contract gate from here.

  <details><summary>Original text</summary>

  `--mode load` asserts upstream-call counts (single-flight 1/1, warm 0/0, mixed corpus 253/253) and
  exits non-zero on a difference. Routing `ListAsync` through the cache *reduces* .NET's upstream
  calls below Java's — a real improvement that the harness will report as a parity failure. Update the
  corpus expectations in the same commit and record it in the known-divergence list, or the next CI
  run blocks on a fix working as designed. Same applies to S6 (`--mode shadow`, 401 body) and L1
  (`--mode shadow`, truncated strings).
- **T6 — `global.json` placement and `rollForward` both bite.** *(Resolved in S4.)* Default
  `rollForward` is `latestPatch`, so pinning `"version": "10.0.302"` fails on a machine carrying
  10.0.4xx — a pin intended to *unblock* contributors instead blocks the ones who are more current.
  Placement: a `global.json` in `dotnet/` is not seen by `dotnet test` run from the repository root,
  and `Middleware.ShadowHarness` is invoked from there. It goes at the repository root, where it has
  no effect on the Maven build beside it.
  ```json
  { "sdk": { "version": "10.0.302", "rollForward": "latestMinor" } }
  ```
  `latestMinor` rather than the `latestFeature` first suggested here: `latestFeature` is pinned to
  major.minor `10.0` and would reject a future 10.1 SDK, which is not the intent. Note also that the
  *version is a floor* — a machine with only 10.0.100 is rejected too. That is deliberate (it records
  the SDK the parity run used) but it is the one way this pin can still block someone, so lower the
  floor rather than loosen `rollForward` if that ever comes up.
- **T7 — Restricting CORS is the moment someone adds `AllowCredentials`.** *(Confirmed and avoided in
  S7.)* `AllowAnyOrigin` and `AllowCredentials` are mutually exclusive, so the old config *could not*
  express the dangerous combination. Switching to `WithOrigins` removes that guardrail, and
  `AllowCredentials` is the reflexive next addition when a browser client misbehaves. This API
  authenticates by bearer token and needs no credentialed requests; adding it would newly enable
  cookie-driven CSRF against endpoints that have never had to consider it. Restrict origins, leave
  credentials off, and say why in a comment beside it.

  > **Measured, both halves.** `new CorsPolicyBuilder().AllowAnyOrigin().AllowCredentials().Build()`
  > throws `InvalidOperationException` — the guardrail is real. `WithOrigins(...).AllowCredentials()`
  > builds with no complaint — so it is genuinely gone the moment the allowlist lands. A comment was
  > therefore not enough: `CorsTests.CredentialsAreNeverAllowed` asserts the absence of
  > `Access-Control-Allow-Credentials` on both a simple response and a preflight, because nothing else
  > in the suite would notice it appearing.
- **T8 — Fixing the surrogate split (L1) breaks parity by design.** Java's `substring` split surrogate
  pairs exactly as C#'s does, so the two services agreed — including on the mangled output. Correcting
  .NET would have created a shadow-corpus divergence that is *correct*, and the trap was that the
  choice might get settled by whichever option made the harness quieter.

  **The harness is gone, so the tension is gone with it — and that is the less comfortable outcome,
  not the more.** L1 is now an ordinary bug fix with nothing arguing against it, which also means
  nothing would have caught it had it been a regression instead. Fix it on its merits and add a unit
  test for the surrogate boundary; the same applies to L2 if the lower-casing is ever changed.

  > **Resolution (L1, done): the trap did not fire, because the thing that would have fired it no
  > longer exists.** Nothing weighed parity against correctness — there was no harness number to keep
  > quiet, so the fix was settled on its merits in one step. What the item confirms is the *second*
  > half of the note, which is the part that outlives it: **the divergence is now unguarded in both
  > directions.** Java truncates `abcdefgh😀…` to a mangled string and .NET no longer does; nothing
  > in the repository records that difference except this document and the tests added with the fix.
  > Had the surrogate split been introduced by a later refactor rather than inherited, no test would
  > have failed — the probe, not the suite, is what caught it, and the probe was written because this
  > note said to look.
  >
  > A concrete instance of the gap: the defect could not have been caught by inspecting a response
  > body either, since the serializer escapes the lone surrogate as `\` + `uFFFD` — six ASCII
  > characters. The measurement that mattered was a deserialize, and that is the shape the
  > replacement test takes.
  >
  > The note's closing line — *"the same applies to L2 if the lower-casing is ever changed"* — was
  > taken literally and is now discharged: `NormalizeTextLowercasesInvariantlyWhateverTheHostLocale`
  > fails if the invariant fold is swapped for the culture-sensitive one, which was the one part of
  > L2's contract nothing was holding.
- **T9 — A "never seed outside Development" guard breaks the integration suite.**
  `MiddlewareApiFactory.cs:63` hosts the app as `Environments.Staging`, and `WithSeedUser(...)` turns
  the seeder on — so the obvious B1 hardening
  (`if (seed.Enabled && !env.IsDevelopment()) throw`) fails host startup for
  `SeededUserCanAuthenticateAgainstTheFreshlyCreatedSchema` and every test built on it. Gate on
  something the tests can opt out of, or move the factory to `Environments.Development` and confirm
  nothing else keys off the environment (`Program.cs:27` selects the console sink from it, and
  `ThrowOnBadRequest` was set explicitly at `:61` *because* the framework default differs by
  environment — that explicit setting is what makes the move safe).

  > **Avoided rather than worked around (B1, done).** Neither escape hatch was needed: the validation
  > added is environment-independent, so there is nothing for the factory's `Staging` to collide
  > with. `SeededUserCanAuthenticateAgainstTheFreshlyCreatedSchema` passes untouched. That was not
  > only convenience — an environment gate is defeated by setting `ASPNETCORE_ENVIRONMENT`, so the
  > design that dodges the trap is also the stronger one. Worth remembering the next time a trap
  > note asks "how do I work around this?": sometimes the answer is that the feature was wrong.
- **T10 — Removing the `demo1234` default breaks the documented startup path.** `.env.example` carries
  `DB_USERNAME`, `DB_PASSWORD` and `JWT_SECRET` but no seed credentials, because they currently have
  compose-level defaults. Switching them to `${VAR:?…}` without adding them to `.env.example` means
  the documented `cp .env.example .env && docker compose up` fails on a fresh clone — trading a
  security bug for an onboarding bug. Both files change together.

  > **Half right (B1, done).** Both files did change together, but the framing was wrong: it assumed
  > `.env.example` should carry *values*. It carries the **keys with no values**, so `cp` alone
  > still fails — deliberately. Writing a working seed password into a tracked file is the original
  > bug wearing a different filename. The onboarding cost is real and was paid: the documented
  > startup is three commands, and the README leads with the two `openssl rand` lines that produce
  > them. The same reasoning was extended to `JWT_SECRET`, which this note did not flag and which
  > shipped a *working* signing key — a bigger hole than the one B1 was written about.

**Acceptance criteria for Phase 6**
- [x] No credential with a committed or defaulted value can authenticate against any non-Development
      configuration; verified by starting the compose stack with an empty `.env` and asserting it
      refuses to start. *(Done — `docker compose config` on a verbatim `.env.example` copy exits 1;
      `StartupFailsFastWhenSeedingIsEnabledWithoutCredentials` covers the in-process path. The one
      committed credential that remains is `DB_USERNAME`/`DB_PASSWORD`, which cannot authenticate
      against the API and reaches only a loopback-bound disposable container — recorded as a
      deliberate exception, not an oversight.)*
- [ ] Cache memory is bounded by something other than the TTL, and a test drives the bound.
- [ ] A price-filter request cannot trigger an unbounded number of distinct full-catalog fetches; a
      test asserts that N requests with varying sub-cent bounds produce ≤ M upstream calls.
- [ ] A test asserts the T1 invariant: inputs sharing a cache key produce the same filtered result.
- [x] Upstream failure injection (5xx, timeout, connection refused) shows retry and circuit-breaker
      behaviour, and `--mode load` confirms the breaker opens rather than queueing. *(Done for the
      first half — `UpstreamResilienceTests` injects a transient 5xx, a persistent 5xx, a slow
      response and an unreachable host against WireMock, and asserts the breaker opens and then
      reaches the upstream zero further times. The `--mode load` half **cannot be met**: the harness
      was deleted with the Java service (T5). The breaker assertion it would have carried is the one
      in `TheCircuitBreakerOpensAndShedsLoadWithoutReachingTheUpstream`, which is a weaker claim —
      it shows shedding, not behaviour under concurrent load.)*
- [ ] `dotnet test` succeeds from a clean clone on a machine with no .NET 10 SDK **or** fails with a
      message naming the required SDK version.
- [ ] The .NET service is documented to the same standard as the Java one.
- [ ] Every harness expectation changed by 6.1–6.3 is updated in the same commit as its change, with
      the divergence recorded — no expectation is relaxed to make a run pass.

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

**R1–R5 are migration risks and are closed by Phases 1–5.** R6–R11 come from the pre-release review
and are open; they are release risks in the shipped design, not porting risks.

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| R6 | ~~**Known-credential account reachable in a Production-configured stack.** The documented compose startup publishes `demo`/`demo1234` with `ASPNETCORE_ENVIRONMENT: Production`, contradicting the intent stated in `appsettings.json`.~~ | ~~**HIGH**~~ **CLOSED** | Phase 6 B1, done. `SEED_USER_PASSWORD` is required via `${VAR:?…}`; `.env.example` declares it (and `JWT_SECRET`) with no value; `SeedUserOptions` fails start-up on blank credentials, so the non-Compose path is covered too. The proposed environment-gated startup guard was rejected — defeated by `ASPNETCORE_ENVIRONMENT`, and it triggers T9. |
| R7 | **Cache-key space is attacker-controlled.** Sub-cent variation in `minPrice`/`maxPrice` produces unbounded distinct keys, each missing the cache, each triggering a full-catalog upstream fetch and retaining a full catalog copy for the TTL. Single-flight gives no protection — the keys differ by construction. | **HIGH** | Phase 6 B2 (quantize + cap) and B3 (bound the cache). Guard the fix with the T1 invariant test; the naïve version cross-contaminates responses. |
| R8 | **Configured cache bound does not exist.** `Cache:MaximumSize` is bound, documented as the Caffeine `maximumSize=500` analog, and never read. The cache is bounded only by the 60s TTL, so the Java service's entry bound was silently dropped in the port. | **HIGH** | Phase 6 B3. Verify HybridCache sizes its L1 entries before setting `SizeLimit` (T2) — the naïve fix converts every cache write into a 500. |
| R9 | ~~**No upstream resilience.** No retry, circuit breaker or concurrency limit. A slow DummyJSON has every request burn the full 5s budget with nothing shedding load — the standard path from a slow dependency to a saturated thread pool.~~ | ~~MED-HIGH~~ **CLOSED** | Phase 6 S1, done. `AddStandardResilienceHandler` on the typed client: 2 retries at a 200 ms exponential base, breaker at 0.5/20 over 30s, 1000-permit concurrency limit, attempt timeout 2s inside an unchanged 5s total. T3's premise did not hold — the handler sets `HttpClient.Timeout` to `InfiniteTimeSpan` itself — but the ordering hazard behind it is pinned by a test. Breaker and retry thresholds were both moved off the library defaults, which measurement showed would never have fired at this traffic level. |
| R10 | **Cache hits pay full deserialization.** `HybridCache` bypasses serialization only for provably-immutable types; `Product`/`ProductPage` are not detected, so every hit deserializes up to 100 products with nested reviews — eroding the benefit the cache was added for. | MED | Phase 6 S2, with the collection members moved to `ImmutableArray<T>` rather than relying on nothing casting `IReadOnlyList<T>` back (T4). |
| R11 | **Abstraction cannot express source capability.** `IProductSource` has no price parameter and uses DummyJSON's `limit=0`-means-everything convention, so a source that filters on price natively is still handed the whole catalog and filtered in memory. Extensible for *swapping* sources, not for *capability*. | MED | Phase 6 L4: replace the five fixed signatures with a `ProductQuery`/`ProductQueryResult` pair. No endpoint or service change; DummyJSON keeps today's behaviour. |

---

*Bottom line: the port is well-scoped — one entity, a synchronous proxy, six endpoints.
Effort concentrates in Phases 3–4 (cache + error/security parity), which is exactly where
the top risks live. The existing test suite is the parity harness: porting it first turns
each phase's acceptance criteria into runnable checks.*

*Post-parity addendum: Phases 1–5 answered "does it behave like the Java service?" — yes, to 79/80
shadow cases and a clean contract diff. Phase 6 answers "should it?" The parity harness is
structurally unable to raise most of §6, because both services agree; three items are inherited
rather than introduced, and clearing them will move harness numbers on purpose (T5, T8). The
concentration is narrow and worth naming: the cache was designed for a small, fixed, trusted catalog,
and its key space was never treated as adversarial input.*
