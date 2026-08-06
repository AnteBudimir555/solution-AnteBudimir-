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
