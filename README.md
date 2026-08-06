# Product Middleware REST API

A middleware REST API that re-exposes products from a third-party source (currently
[DummyJSON](https://dummyjson.com)) through a clean, filterable, JWT-protected API.

The service sits **in front of** the upstream product source and exposes a trimmed, cached product
API to clients. The upstream is reached only through an `IProductSource` abstraction, so additional
source types (other web services, a database, the file system, RSS, …) can be added later without
touching the endpoints, service, or DTOs — DummyJSON is just one concrete implementation.

---

## Table of contents

- [Tech stack](#tech-stack)
- [Architecture](#architecture)
- [Endpoints](#endpoints)
- [Running locally](#running-locally)
  - [SQLite, zero setup](#1-sqlite--zero-setup)
  - [Docker Compose (app + Postgres)](#2-docker-compose-app--postgres)
  - [Postgres without Docker](#3-postgres-without-docker)
- [Authentication & test user](#authentication--test-user)
- [Configuration reference](#configuration-reference)
- [Caching / request deduplication](#caching--request-deduplication)
- [Filtering & search push-down](#filtering--search-push-down)
- [Error handling](#error-handling)
- [Logging](#logging)
- [API documentation (Swagger)](#api-documentation-swagger)
- [Testing](#testing)
- [Project structure](#project-structure)
- [AI usage disclosure](#ai-usage-disclosure)

---

## Tech stack

| Concern            | Choice                                                                    |
|--------------------|---------------------------------------------------------------------------|
| Language / runtime | C# on .NET 10 (SDK pinned by `global.json`)                               |
| Framework          | ASP.NET Core minimal APIs                                                 |
| Build tool         | .NET SDK (`dotnet build` / `dotnet test`)                                 |
| Persistence        | EF Core — SQLite (local dev), PostgreSQL (Docker / production-like)       |
| Security           | JWT bearer (`Microsoft.IdentityModel`), BCrypt password hashing           |
| Caching            | `HybridCache` (single-flight, in-memory)                                  |
| Validation         | FluentValidation                                                          |
| API docs           | Swashbuckle (Swagger UI + OpenAPI document)                               |
| Logging            | Serilog, correlation id via `LogContext`, compact JSON outside Development |
| Tests              | xUnit, NSubstitute, WireMock.Net, Testcontainers, `WebApplicationFactory` |

> **Toolchain:** `global.json` pins the SDK to 10.0.302 (rolling forward within .NET 10). A machine
> without a matching SDK gets an error naming the required version rather than an obscure build
> failure.

---

## Architecture

```
Client ─▶ ProductEndpoints ─▶ ProductService ─▶ ProductQueryCache ─▶ IProductSource (interface)
                                   │              (HybridCache)             │
                                   └─ ProductMapper (domain → DTO)          └─ DummyJsonProductSource
                                                                              (typed HttpClient → DummyJSON)
```

Three projects, with dependencies pointing inward (`Api → Infrastructure → Core`):

- **`IProductSource`** (`src/Middleware.Core/Abstractions`) — the extension point. Returns internal
  **domain** types (`Product`, `ProductPage`), never upstream types. Catalog reads take a
  `ProductQuery` and return a `ProductQueryResult` saying which parts of it the source applied, so a
  source that can filter on price natively can say so (`SupportsPriceFilter`) instead of being handed
  the whole catalog to filter in process.
- **`DummyJsonProductSource`** (`src/Middleware.Infrastructure/Upstream`) — the only current
  implementation. Upstream JSON is isolated in `Upstream/Dto/*` (all `internal`) and mapped to the
  domain by `DummyProductMapper`. Because `Core` does not reference `Infrastructure`, a leak of an
  upstream type into the application layer is a compile error rather than a review catch.
- **`ProductService`** — orchestration: assembling the response and mapping to DTOs.
- **`ProductQueryCache`** — a separate class holding the cached queries, so the single-flight cache
  and the pagination-independent candidate set are both honoured. It also decides *how* to ask for a
  price-filtered page — push the bounds down or fetch a candidate set and filter in memory — because
  that choice determines the cache key, and the key has to be picked before the call.
- **DTOs** — a trimmed `ProductSummaryDto` (list/filter/search) and a full `ProductDetailDto` (detail).

Adding a second source means writing one class and changing one DI registration
(`AddUpstreamSource` in `Program.cs`); no endpoint or service changes.

---

## Endpoints

All responses are JSON. Every `/api/products/**` endpoint requires an `Authorization: Bearer <token>`
header; `/api/auth/login` and the Swagger docs are public.

| Method | Path                       | Description                                        | Auth |
|--------|----------------------------|----------------------------------------------------|:----:|
| POST   | `/api/auth/login`          | Exchange username/password for a JWT               |  —   |
| GET    | `/api/products`            | Paged, trimmed product list                        |  ✔   |
| GET    | `/api/products/{id}`       | Full detail of a single product                    |  ✔   |
| GET    | `/api/products/filter`     | Filter by `category` and/or `minPrice`/`maxPrice`  |  ✔   |
| GET    | `/api/products/search`     | Free-text search by product name (`q`)             |  ✔   |
| GET    | `/api/products/categories` | Available category identifiers                     |  ✔   |

**Common query params** (list/filter/search): `page` (0-based, default `0`, max `10000`), `size`
(1–100, default `20`). The trimmed shape is `{ image, name, price, shortDescription }` where
`shortDescription` is hard-capped at 100 characters on a word boundary.

### Example session

```bash
# 1. Log in and capture the token. These are the Development seed credentials; under Compose the
#    username is the same but the password is the one you generated into .env.
TOKEN=$(curl -s -X POST http://localhost:8080/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"demo","password":"demo1234"}' | jq -r .token)

# 2. Trimmed, paginated list
curl -s http://localhost:8080/api/products?page=0\&size=5 \
  -H "Authorization: Bearer $TOKEN"

# 3. Full product detail
curl -s http://localhost:8080/api/products/1 -H "Authorization: Bearer $TOKEN"

# 4. Filter: category + price range (combinable)
curl -s "http://localhost:8080/api/products/filter?category=smartphones&minPrice=100&maxPrice=1000" \
  -H "Authorization: Bearer $TOKEN"

# 5. Search by name
curl -s "http://localhost:8080/api/products/search?q=phone" -H "Authorization: Bearer $TOKEN"
```

---

## Running locally

**Prerequisites:** the .NET 10 SDK (see `global.json`). Docker is needed only for the Compose path
and for the one Testcontainers-backed test, which skips itself when Docker is absent.

### 1. SQLite — zero setup

The default. A self-contained SQLite file, and — under the `Development` environment only — a
pre-seeded `demo` user. No external services or configuration required.

```bash
dotnet run --project src/Middleware.Api
```

- App: `http://localhost:8080` (or whatever `ASPNETCORE_URLS` / launch settings select)
- Swagger UI: `http://localhost:8080/swagger`
- Database: `middleware-dev.db` in the working directory (gitignored, created on first run)
- Seed user: **`demo` / `demo1234`**

### 2. Docker Compose (app + Postgres)

Builds the image and starts the app against a Postgres container.

```bash
cp .env.example .env
printf 'JWT_SECRET=%s\n'         "$(openssl rand -base64 48)" >> .env
printf 'SEED_USER_PASSWORD=%s\n' "$(openssl rand -base64 18)" >> .env
docker compose up --build
```

**Three commands, not one, on purpose.** This stack runs `ASPNETCORE_ENVIRONMENT=Production` and
publishes port 8080. Every value that can authenticate against it — the JWT signing key and the
seed-user password — is declared in `.env.example` with **no value** and required in
`docker-compose.yml` with `${VAR:?…}`. Copying the example alone therefore stops with a readable
error rather than starting a production-profile service on credentials that are readable in this
repository. Fill both in and it starts normally.

Log in with `demo` and whatever password you generated (override the username with
`SEED_USER_USERNAME`). To run with no seeded account at all, set `Security__SeedUser__Enabled` to
`false` in a compose override file and manage users out of band — which is what a real deployment
should do.

`DB_USERNAME` / `DB_PASSWORD` do keep throwaway values in `.env.example`. They are a different
class: the database is reachable only over the internal Compose network and a loopback-bound host
port, it holds nothing but the demo user table, and the volume is disposable.

You can also supply values per invocation instead of via `.env`:

```bash
JWT_SECRET='a-strong-secret-at-least-32-bytes-long' \
SEED_USER_PASSWORD='something-you-generated' \
  docker compose up --build
```

The app enforces the same rules independently of Compose, so running the image by hand is no
weaker: `Jwt:Secret` is validated at startup, the Postgres provider has no default connection
string, and enabling the seeder with a blank username or password fails validation on start rather
than creating an account anyone can log into.

### 3. Postgres without Docker

Point the app at your own Postgres. Configuration keys nest with `:` in files and `__` in
environment variables:

```bash
export Database__Provider='Postgres'
export ConnectionStrings__Default='Host=localhost;Port=5432;Database=middleware;Username=middleware;Password=middleware'
export Jwt__Secret='a-strong-secret-at-least-32-bytes-long'
# Optional: opt into a seed user for testing. Both values are required once Enabled is true —
# a blank one fails validation at startup instead of creating a wide-open account.
export Security__SeedUser__Enabled=true
export Security__SeedUser__Username=demo
export Security__SeedUser__Password='choose-something-non-obvious'
dotnet run --project src/Middleware.Api
```

Under Postgres the schema is applied with EF Core migrations at startup; under SQLite it is created
directly (the dev database carries no migration history).

---

## Authentication & test user

Auth is **token-based (JWT, HS256)** with a local EF Core user table (`UserAccount`), BCrypt-hashed
passwords, and stateless bearer authentication.

1. **Get a token** — `POST /api/auth/login` with `{"username": "...", "password": "..."}`.
   The response is `{ "token", "tokenType": "Bearer", "expiresInSeconds" }`.
2. **Call protected endpoints** — send `Authorization: Bearer <token>`.

**Test user:** the `Development` environment seeds **`demo` / `demo1234`** — a known credential, and
deliberately so: it lives in `appsettings.Development.json`, which is loaded only under
`ASPNETCORE_ENVIRONMENT=Development`. The Compose stack also seeds `demo`, but with the password
*you* supply via `SEED_USER_PASSWORD`; it ships none. Bad credentials return `401`; a
missing/expired/invalid token on a protected endpoint returns `401`.

Validation is deliberately strict: the algorithm is pinned to HS256 (so no algorithm-confusion
downgrade), the issuer is enforced, there is **no clock skew**, and a token whose subject no longer
exists in the user store is rejected even if its signature is valid.

> The JWT secret is never shipped with a default. It must be provided via configuration and must be
> **≥ 32 characters** for HS256, otherwise the app fails fast at startup.

---

## Configuration reference

Configuration binds to typed options classes. Any key can be supplied by environment variable using
`__` as the separator (`Jwt__Secret` ⇒ `Jwt:Secret`).

| Key                              | Default                     | Purpose                                       |
|----------------------------------|-----------------------------|-----------------------------------------------|
| `Database:Provider`              | `Sqlite`                    | `Sqlite` or `Postgres`                        |
| `ConnectionStrings:Default`      | `Data Source=middleware-dev.db` (SQLite) | DB connection; **required** under Postgres |
| `Jwt:Secret`                     | — (required, ≥ 32 chars)    | HS256 signing key                             |
| `Jwt:ExpirationMinutes`          | `60`                        | Token lifetime                                |
| `Jwt:Issuer`                     | `abysalto-middleware`       | Issued and enforced on validation             |
| `Upstream:BaseUrl`               | `https://dummyjson.com`     | Upstream source base URL                      |
| `Upstream:ConnectTimeoutMs`      | `3000`                      | TCP/TLS connect timeout                       |
| `Upstream:ResponseTimeoutMs`     | `5000`                      | Budget for the **whole** call including retries |
| `Upstream:AttemptTimeoutMs`      | `2000`                      | Budget for one attempt; must be < `ResponseTimeoutMs` |
| `Upstream:MaxInMemoryCandidates` | `5000`                      | Ceiling on an in-memory price filter; exceeding it fails the request |
| `Upstream:RetryAttempts`         | `2`                         | Retries after the first attempt (minimum `1`) |
| `Upstream:RetryBaseDelayMs`      | `200`                       | First retry delay; exponential with jitter after |
| `Upstream:CircuitBreakerFailureRatio` | `0.5`                  | Failing share of attempts that opens the breaker |
| `Upstream:CircuitBreakerMinimumThroughput` | `20`              | Attempts needed in the window before the ratio applies |
| `Upstream:CircuitBreakerSamplingDurationMs` | `30000`          | Window the ratio is measured over; must be ≥ 2× `AttemptTimeoutMs` |
| `Upstream:CircuitBreakerBreakDurationMs` | `5000`                | How long the breaker sheds load before probing again |
| `Upstream:PooledConnectionLifetimeMinutes` | `5`               | Connection recycling, so upstream DNS changes are picked up |
| `Cache:MaximumSizeBytes`         | `67108864` (64 MiB)         | Total cache byte budget (**bytes, not entries**) |
| `Cache:MaximumEntryBytes`        | `1048576` (1 MiB)           | Largest single cacheable entry                |
| `Cache:ExpireAfterWriteSeconds`  | `60`                        | Entry TTL                                     |
| `Cache:CategoriesExpireAfterWriteSeconds` | `3600`             | TTL for the category list, which ages far more slowly |
| `Summary:DescriptionMaxLength`   | `100`                       | `shortDescription` cap                        |
| `Security:SeedUser:Enabled`      | `false`                     | Create the seed user on startup               |
| `Security:SeedUser:Username`     | —                           | Seed user name (required when enabled)        |
| `Security:SeedUser:Password`     | —                           | Seed user password (required when enabled)    |
| `Cors:AllowedOrigins`            | *(empty — no browser origin)* | Exact origins allowed to read responses; see below |

Environments:
- **`Development`** — SQLite, seed user on (`demo`/`demo1234`), a throwaway JWT secret, readable
  console logs. See `appsettings.Development.json`.
- **Anything else** — compact JSON logs, no default secret, seeding off unless explicitly enabled,
  and no way to enable it without supplying credentials.

### CORS

`Cors:AllowedOrigins` is an allowlist and **defaults to empty**, which allows no browser origin at
all — the right default for an API whose callers are server-side. A deployment that serves a browser
client names its origins:

```jsonc
"Cors": { "AllowedOrigins": [ "https://app.example.com", "http://localhost:5173" ] }
```

or, by environment variable, `Cors__AllowedOrigins__0=https://app.example.com`.

Entries are **exact origins**: scheme, host, and port if non-default — no trailing slash and no path.
Every other spelling is accepted by the framework and then matches nothing, with no error and no log
line, so the app validates them at startup and refuses to start on a bad one. Host casing is
normalized and matches either way.

**Credentials are never allowed**, and that is a fixed decision rather than a default. This API
authenticates by bearer token; enabling `AllowCredentials` would expose endpoints to cookie-driven
CSRF that have never had to consider it.

> **What this does not do.** CORS is enforced by the browser. A request from a disallowed origin
> still reaches the endpoint, runs it, and returns the full body — the absent
> `Access-Control-Allow-Origin` header only stops the calling *script* from reading it, and a
> non-browser client ignores the mechanism entirely. This bounds which web pages can use the API from
> a visitor's browser. It is not an access control, and a stolen token still works from anywhere:
> authentication remains the only thing guarding the data.

---

## Caching / request deduplication

Repeated **search** and **filter** calls with the same parameters are served from an in-memory
`HybridCache` instead of re-hitting the upstream.

- **Single-flight:** concurrent callers for the same key share one upstream fetch, so a cold cache
  under load produces one request rather than a stampede.
- **Normalized keys:** built by `CacheKeys` so semantically-identical requests collapse to one entry
  — text is trimmed and lower-cased. The same normalization feeds both the cache key and the actual
  upstream call, so the two can never diverge.
- **Pagination- and price-independent candidate cache:** for price-filtered queries the *unfiltered*
  candidate set is cached **by category alone**, and the price bounds are applied in memory over it.
  The upstream call never depended on the bounds, so keying on them would have meant a full catalog
  fetch per distinct pair — with client-supplied decimals, that is unbounded. Paging through a
  filtered result, or re-filtering the same category by any other range, reuses one fetch.
- **Bounded:** the cache is capped by a byte budget (`Cache:MaximumSizeBytes`) as well as the TTL.
  Note the unit — it is bytes, not entry count.
- **Cached values are immutable:** the domain records are marked `[ImmutableObject(true)]` and their
  collections are frozen at the mapper, so the cache can hand one instance to every caller instead of
  deserializing the entry on each hit.

**What is and is not cached:**

| Endpoint | Cached | TTL |
|---|---|---|
| `GET /api/products` | yes — shares its entry with an unfiltered `/filter` | `Cache:ExpireAfterWriteSeconds` |
| `GET /api/products/filter` | yes | `Cache:ExpireAfterWriteSeconds` |
| `GET /api/products/search` | yes | `Cache:ExpireAfterWriteSeconds` |
| `GET /api/products/categories` | yes | `Cache:CategoriesExpireAfterWriteSeconds` |
| `GET /api/products/{id}` | **no** — deliberately | — |

The listing and an unfiltered `/filter` issue the identical upstream call, so they share one entry;
serving byte-identical data from cache or not depending on which URL the client picked was arbitrary.

Categories get a TTL of their own because they age differently: product pages go stale as stock and
prices move, whereas the set of categories a source exposes changes approximately never.

`GET /api/products/{id}` stays uncached on purpose. A product's own record is what a client reads
before acting on it, and stock and price are exactly the fields most likely to have moved — a stale
page costs a client little, a stale detail can cost it an order. Revisit only alongside a way to
invalidate the entry.

---

## Filtering & search push-down

Filters are pushed to the upstream where DummyJSON supports it, and applied in-service otherwise
(documented in `DummyJsonProductSource`):

| Capability      | Where it runs | Upstream used                             |
|-----------------|---------------|-------------------------------------------|
| Pagination      | Upstream      | `?limit=&skip=`                           |
| Name search     | Upstream      | `/products/search?q=`                     |
| Category filter | Upstream      | `/products/category/{slug}`               |
| Price range     | In-service    | not supported upstream — filtered locally |

Category and price filters are **combinable**: the category is pushed down, then the price range is
applied to the returned candidate set. If that set exceeds `Upstream:MaxInMemoryCandidates` the
request fails with a `502` rather than materializing an unbounded catalog in memory.

Name search and category filtering are pushed down **one at a time** — DummyJSON has no endpoint that
intersects them, so a query asking for both is refused rather than answered with a superset.

The last row is a property of *this* source, not of the middleware. A source that can filter on price
in its own store declares `SupportsPriceFilter`, and is then sent the bounds and the page directly —
no candidate set is fetched, held, or measured against `MaxInMemoryCandidates`, since that threshold
exists to bound an in-memory materialization that no longer happens.

---

## Upstream resilience

The typed upstream client carries a Polly pipeline (`AddStandardResilienceHandler`): concurrency
limit → total timeout → retry → circuit breaker → attempt timeout.

- **Retry** — 2 retries after the first attempt, 200 ms base, exponential with jitter. Every upstream
  call is a `GET`, so retrying is safe. Transient statuses only: `408`, `429` and `5xx` are retried,
  `404` is not — a missing product is an answer, and retrying it would triple the upstream cost of a
  request any client can repeat.
- **Circuit breaker** — opens once half the attempts in a 30 s window fail, with at least 20 attempts
  seen, then sheds load for 5 s before probing again. Both thresholds are deliberately away from the
  library defaults (`0.1` / `100`): at this traffic level a 100-attempt threshold would never be
  reached, and a 10% ratio treats ordinary upstream weather as an outage.
- **Two timeouts, and the split is the point.** `ResponseTimeoutMs` bounds the whole call *including*
  retries, and is unchanged from before the pipeline existed; `AttemptTimeoutMs` bounds one attempt.
  Retries fit inside the worst case clients already faced, so **adding resilience did not make a dead
  upstream slower to report**. The cost paid for that: an upstream that is slow rather than failing
  gets roughly two and a half attempts before the total timeout cuts the sequence off.
- **`HttpClient.Timeout` is `InfiniteTimeSpan`** — the pipeline owns timeouts. A client timeout
  applied after the resilience handler overrides it and silently caps the whole retry sequence at one
  attempt; `RetriesAreNotCancelledByAClientTimeout` pins this.

Everything the pipeline gives up on — retries exhausted, breaker open, timeout — reaches
`DummyJsonProductSource` as an exception and surfaces as a `502`, the same as before.

---

## Error handling

All errors are returned as **RFC 7807 `application/problem+json`**. Authentication and authorization
failures happen inside the middleware pipeline, before any endpoint runs, but are rendered through
the same writer — so every error shares one consistent shape, including framework-generated
responses such as an unknown route or an unsupported method.

| Situation                                  | Status | `detail`                            |
|--------------------------------------------|:------:|-------------------------------------|
| Validation / malformed params              | `400`  | the constraint messages             |
| Bad login                                  | `401`  | `Invalid username or password.`     |
| Missing/invalid/expired token              | `401`  | `Missing or invalid bearer token.`  |
| Authenticated but not permitted            | `403`  | `Access Denied`                     |
| Unknown product / route                    | `404`  | what was not found                  |
| Upstream source failure or timeout         | `502`  | `The product source is currently unavailable.` |
| Unexpected server error                    | `500`  | `An unexpected error occurred.`     |

The two `401`s are worded differently on purpose. Login stays generic because distinguishing "no such
user" from "wrong password" enables account enumeration; the token challenge has no username to
enumerate, so it names the mechanism that rejected you — the difference between "re-authenticate" and
"my password is wrong". It stays uniform across *why* the token failed (absent, malformed, expired,
forged, or issued for a since-deleted user), which is what keeps it from leaking anything.

Raw exception messages are never returned; upstream failures and unexpected errors render fixed,
client-safe text and log the detail server-side.

---

## Logging

Structured Serilog logging at appropriate levels:

- `CorrelationIdMiddleware` assigns a **correlation id** per request (Serilog `LogContext`), echoes it
  on the response as `X-Correlation-Id`, and logs method, path, status and duration. An inbound id is
  reused only when it matches a strict safe pattern, which also blocks log injection.
- **Development** uses a readable console pattern including the correlation id.
- **Other environments** emit one compact **JSON** object per line, with `correlationId` / `method` /
  `path` / `status` / `durationMs` as discrete fields for aggregation.
- Secrets are never logged — the auth flow logs only the username, never the password or the token,
  and no request headers or bodies are logged anywhere.

---

## API documentation (Swagger)

Swashbuckle generates the OpenAPI document and serves Swagger UI, with a JWT bearer scheme wired in
so you can authorize and try protected endpoints directly:

- Swagger UI: `http://localhost:8080/swagger`
- OpenAPI JSON: `http://localhost:8080/swagger/v1/swagger.json`

Click **Authorize**, paste the token from `/api/auth/login`, and invoke any endpoint. Public endpoints
carry no lock; the common RFC-7807 error responses are declared on every operation.

---

## Testing

```bash
dotnet test                                              # everything
dotnet test tests/Middleware.UnitTests                   # unit only
dotnet test tests/Middleware.IntegrationTests            # integration only
```

Current state: **136 passing** (68 unit + 68 integration), Release build warning-free under
`TreatWarningsAsErrors`.

- **Unit tests** cover truncation boundaries, DTO mapping, the price filter, cache-key normalization
  and single-flight behaviour, JWT round-trips (including tampered/expired/wrong-issuer rejection),
  and the paged-response envelope.
- **Integration tests** host the real pipeline with `WebApplicationFactory<Program>`, substituting
  only the network boundary (`IProductSource`), so the auth flow, the RFC-7807 contract, request
  validation and the OpenAPI document are all exercised as a client would hit them. The upstream
  adapter is covered separately against WireMock.Net.
- One test uses **Testcontainers** to apply the EF migration to a real PostgreSQL container; it skips
  itself when Docker is unavailable.

---

## Project structure

```
.
├── src/
│   ├── Middleware.Core/            # domain, DTOs, options, application services — no I/O
│   │   ├── Abstractions/           # IProductSource — the extension point
│   │   ├── Common/                 # CacheKeys, TextUtils
│   │   ├── Domain/                 # Product, ProductPage, Review, Meta, Dimensions
│   │   ├── Dtos/                   # summary, detail, paged envelope, auth
│   │   ├── Exceptions/             # domain exceptions
│   │   ├── Options/                # typed configuration
│   │   └── Services/               # ProductService, ProductQueryCache, ProductMapper
│   ├── Middleware.Infrastructure/  # everything that talks to the outside world
│   │   ├── Persistence/            # EF Core context, UserAccount, repository, migrations
│   │   ├── Security/               # JwtService, BCrypt hasher, user seeder
│   │   └── Upstream/               # DummyJSON adapter + isolated upstream DTOs
│   └── Middleware.Api/             # the web host
│       ├── Endpoints/              # product and auth endpoints
│       ├── Errors/                 # RFC-7807 problem body, exception handler
│       ├── Middleware/             # correlation id, routing parity
│       ├── OpenApi/                # Swagger document configuration
│       ├── Security/               # bearer authentication wiring
│       ├── Serialization/          # JSON converters
│       └── Validation/             # FluentValidation rules, query parsing
└── tests/
    ├── Middleware.UnitTests/
    └── Middleware.IntegrationTests/
```

`MIGRATION_PLAN.md` records how this service was ported from its original Spring Boot implementation,
and tracks the remaining pre-release hardening items (Phase 6).

---

## AI usage disclosure

This project was built with the assistance of **Claude Code** (Anthropic). All generated code was reviewed and adjusted incrementally, with progress reflected in
the git history. Design intent and rationale are captured in code comments throughout.
