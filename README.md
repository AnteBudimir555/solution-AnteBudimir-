# Product Middleware REST API

A middleware REST API that re-exposes products from a third-party source (currently
[DummyJSON](https://dummyjson.com)) through a clean, filterable, JWT-protected API.

The service sits **in front of** the upstream product source and exposes a trimmed, cached product
API to clients. The upstream is reached only through a `ProductSource` abstraction, so additional
source types (other web services, a database, the file system, RSS, …) can be added later without
touching the controllers, service, or DTOs — DummyJSON is just one concrete implementation.

---

## Table of contents

- [Tech stack](#tech-stack)
- [Architecture](#architecture)
- [Endpoints](#endpoints)
- [Running locally](#running-locally)
  - [Dev profile (H2, zero setup)](#1-dev-profile-h2--zero-setup)
  - [Docker Compose (app + Postgres)](#2-docker-compose-app--postgres)
  - [Postgres profile without Docker](#3-postgres-profile-without-docker)
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

| Concern            | Choice                                                             |
|--------------------|--------------------------------------------------------------------|
| Language / runtime | Java 21 (release level), built on JDK 25                           |
| Framework          | Spring Boot 4.1.0 (Spring Web MVC, synchronous `RestClient`)       |
| Build tool         | Maven (via the bundled `./mvnw` wrapper)                           |
| Persistence        | Spring Data JPA / Hibernate — H2 (dev/test), PostgreSQL (Docker)   |
| Security           | Spring Security, stateless JWT (jjwt), BCrypt password hashing     |
| Caching            | Spring Cache backed by Caffeine                                    |
| API docs           | springdoc-openapi (Swagger UI)                                     |
| Logging            | SLF4J / Logback, MDC correlation id, JSON logs in the prod profile |
| Tests              | JUnit 5, Mockito, `MockRestServiceServer`, `MockMvc`              |

> **Why Maven:** the project uses the Maven wrapper (`./mvnw`), so no local Maven install is needed.

---

## Architecture

```
Client ─▶ ProductController ─▶ ProductService ─▶ ProductQueryCache ─▶ ProductSource (interface)
                                    │                (Caffeine)              │
                                    └─ ProductMapper (domain → DTO)          └─ DummyJsonProductSource
                                                                                (RestClient → DummyJSON)
```

- **`ProductSource`** (`source/ProductSource.java`) — the extension point. Returns internal **domain**
  types (`Product`, `ProductPage`), never upstream types.
- **`DummyJsonProductSource`** — the only current implementation. Upstream JSON is isolated in
  `source/dummyjson/dto/*` and mapped to the domain by `DummyProductMapper`.
- **`ProductService`** — orchestration: pagination, DTO mapping, and the in-service price filter.
- **`ProductQueryCache`** — a separate bean holding the `@Cacheable` methods (so Spring's cache proxy
  is honoured and not bypassed by self-invocation).
- **DTOs** — a trimmed `ProductSummaryDto` (list/filter/search) and a full `ProductDetailDto` (detail).

---

## Endpoints

All responses are JSON. Every `/api/products/**` endpoint requires a `Authorization: Bearer <token>`
header; `/api/auth/login` and the Swagger docs are public.

| Method | Path                       | Description                                             | Auth |
|--------|----------------------------|---------------------------------------------------------|:----:|
| POST   | `/api/auth/login`          | Exchange username/password for a JWT                    |  —   |
| GET    | `/api/products`            | Paged, trimmed product list                             |  ✔   |
| GET    | `/api/products/{id}`       | Full detail of a single product                         |  ✔   |
| GET    | `/api/products/filter`     | Filter by `category` and/or `minPrice`/`maxPrice`       |  ✔   |
| GET    | `/api/products/search`     | Free-text search by product name (`q`)                  |  ✔   |
| GET    | `/api/products/categories` | Available category identifiers                          |  ✔   |

**Common query params** (list/filter/search): `page` (0-based, default `0`), `size` (1–100, default `20`).
The trimmed shape is `{ image, name, price, shortDescription }` where `shortDescription` is hard-capped
at 100 characters on a word boundary.

### Example session

```bash
# 1. Log in (dev seed user) and capture the token
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

**Prerequisites:** JDK 21+ (JDK 25 recommended to match the toolchain). Docker is only needed for the
Compose path. The Maven wrapper (`./mvnw` / `mvnw.cmd`) handles Maven itself.

### 1. Dev profile (H2, zero setup)

The default profile. Embedded in-memory H2, an enabled H2 console, and a pre-seeded `demo` user — no
external services or configuration required.

```bash
./mvnw spring-boot:run
```

- App: `http://localhost:8080`
- Swagger UI: `http://localhost:8080/swagger-ui.html`
- H2 console: `http://localhost:8080/h2-console` (JDBC URL `jdbc:h2:mem:middleware`, user `sa`, no password)
- Seed user: **`demo` / `demo1234`**

### 2. Docker Compose (app + Postgres)

Builds the image and starts the app under the `postgres` profile against a Postgres container.

```bash
cp .env.example .env      # then edit JWT_SECRET for anything real
docker compose up --build
```

Compose auto-loads `.env` (copied from the committed `.env.example`) for `DB_USERNAME`,
`DB_PASSWORD`, and `JWT_SECRET`. The `postgres` profile has **no defaults** for these and fails fast
if any is missing — and `docker-compose.yml` mirrors that for `JWT_SECRET`, so a known signing key is
never baked into a tracked file. The `.env.example` values are throwaway demo secrets; generate a
real one for any real deployment (`openssl rand -base64 48`). You can also override per-invocation
from the shell:

```bash
JWT_SECRET='a-strong-secret-at-least-32-bytes-long' docker compose up --build
```

For the Compose demo, seeding is explicitly enabled (`SEED_USER_ENABLED=true`) so the protected API is
testable immediately with **`demo` / `demo1234`**. In a real deployment, leave seeding off and manage
users out of band.

### 3. Postgres profile without Docker

Point the app at your own Postgres and provide the required env vars:

```bash
export SPRING_PROFILES_ACTIVE=postgres
export DB_URL='jdbc:postgresql://localhost:5432/middleware'
export DB_USERNAME='middleware'
export DB_PASSWORD='middleware'
export JWT_SECRET='a-strong-secret-at-least-32-bytes-long'
# Optional: opt into a seed user for testing
export SEED_USER_ENABLED=true SEED_USER_USERNAME=demo SEED_USER_PASSWORD=demo1234
./mvnw spring-boot:run
```

---

## Authentication & test user

Auth is **token-based (JWT)** with a local JPA user table (`UserAccount`), BCrypt-hashed passwords,
and a stateless Spring Security filter chain.

1. **Get a token** — `POST /api/auth/login` with `{"username": "...", "password": "..."}`.
   The response is `{ "token", "tokenType": "Bearer", "expiresInSeconds" }`.
2. **Call protected endpoints** — send `Authorization: Bearer <token>`.

**Test user:** the dev profile and the Docker Compose demo both seed **`demo` / `demo1234`**. Bad
credentials return `401`; a missing/expired/invalid token on a protected endpoint returns `401`.

> The JWT secret is never shipped with a default. It must be provided via `JWT_SECRET` (or the dev
> profile's throwaway value) and must be **≥ 32 characters** for HS256, otherwise the app fails fast
> at startup.

---

## Configuration reference

Configuration is bound to typed `@ConfigurationProperties` and driven by environment variables.

| Env var                    | Profile(s)     | Default (base)                     | Purpose                                        |
|----------------------------|----------------|------------------------------------|------------------------------------------------|
| `SPRING_PROFILES_ACTIVE`   | all            | `dev`                              | Active profile (`dev` / `postgres` / `test`)   |
| `SERVER_PORT`              | all            | `8080`                             | HTTP port                                      |
| `JWT_SECRET`               | all            | — (dev has a throwaway value)      | HS256 signing key (**≥ 32 chars**, required)   |
| `JWT_EXPIRATION_MINUTES`   | all            | `60`                               | Token lifetime                                 |
| `DUMMYJSON_BASE_URL`       | all            | `https://dummyjson.com`            | Upstream source base URL                       |
| `DUMMYJSON_CONNECT_TIMEOUT_MS` | all        | `3000`                             | Upstream connect timeout                       |
| `DUMMYJSON_RESPONSE_TIMEOUT_MS`| all        | `5000`                             | Upstream response timeout                      |
| `DB_URL`                   | postgres       | `jdbc:postgresql://localhost:5432/middleware` | JDBC URL                            |
| `DB_USERNAME`              | postgres       | — (required)                       | DB user                                        |
| `DB_PASSWORD`              | postgres       | — (required)                       | DB password                                    |
| `SEED_USER_ENABLED`        | all            | `false` (`true` in dev)            | Create the seed user on startup                |
| `SEED_USER_USERNAME`       | all            | — (`demo` in dev)                  | Seed user name                                 |
| `SEED_USER_PASSWORD`       | all            | — (`demo1234` in dev)              | Seed user password                             |

Profiles:
- **`dev`** — H2 in-memory, H2 console on, seed user on (`demo`/`demo1234`), a throwaway JWT secret,
  human-readable console logs. Default.
- **`postgres`** — production-like: Postgres, JSON logs, no defaults for DB creds / JWT secret
  (fail-fast), seeding off unless explicitly enabled.
- **`test`** — H2, used by the automated tests.

---

## Caching / request deduplication

Repeated **search** and **filter** calls with the same parameters are served from an in-memory
**Caffeine** cache (Spring Cache abstraction) instead of re-hitting the upstream.

- **Backend / TTL:** Caffeine, `maximumSize=500, expireAfterWrite=60s` (`middleware.cache.spec`).
- **Normalized keys:** cache keys are built by `CacheKeys` so semantically-identical requests collapse
  to one entry — text is trimmed/lower-cased, and numeric price scales are normalized (`10` == `10.00`).
  The same normalization feeds both the cache key and the actual upstream call, so they can never diverge.
- **Pagination-independent filter cache:** for price-filtered queries the full candidate set is cached
  by *category + price bounds only* (not by page), so paging through a filtered result reuses one
  upstream fetch instead of re-fetching per page.
- **Correctness:** the `@Cacheable` methods live in a dedicated `ProductQueryCache` bean so Spring's
  cache proxy is always applied (self-invocation from `ProductService` would otherwise bypass it).

---

## Filtering & search push-down

Filters are pushed to the upstream where DummyJSON supports it, and applied in-service otherwise
(documented in `DummyJsonProductSource`):

| Capability     | Where it runs | Upstream used                          |
|----------------|---------------|-----------------------------------------|
| Pagination     | Upstream      | `?limit=&skip=`                         |
| Name search    | Upstream      | `/products/search?q=`                   |
| Category filter| Upstream      | `/products/category/{slug}`             |
| Price range    | In-service    | not supported upstream — filtered locally |

Category and price filters are **combinable**: the category is pushed down, then the price range is
applied to the returned candidate set. A candidate-count guard logs a warning if an unusually large set
is materialized in memory (safe for DummyJSON's small catalog; a larger source should push the price
filter down instead).

---

## Error handling

All errors are returned as **RFC 7807 `application/problem+json`** via a single
`GlobalExceptionHandler`. Security failures (401/403) occur inside the filter chain but are delegated
to the same handler, so every error shares one consistent shape.

| Situation                                  | Status |
|--------------------------------------------|:------:|
| Validation / malformed params              | `400`  |
| Missing/invalid/expired token, bad login   | `401`  |
| Authenticated but not permitted            | `403`  |
| Unknown product / route                    | `404`  |
| Upstream source failure or timeout         | `502`  |
| Unexpected server error                    | `500`  |

---

## Logging

Structured SLF4J/Logback logging at appropriate levels (INFO/WARN/ERROR):

- A `RequestLoggingFilter` assigns a **correlation id** per request (MDC) and logs method, path, status,
  and duration.
- **Non-prod profiles** use a readable console pattern including the correlation id.
- The **`postgres` profile** emits one **JSON** object per line (Logstash encoder) with
  `correlationId` / `method` / `path` / `status` / `durationMs` as discrete fields for aggregation.
- Secrets are never logged — the auth flow logs only the username, never the password or the token.

---

## API documentation (Swagger)

springdoc-openapi generates the OpenAPI spec and Swagger UI, with a JWT bearer scheme wired in so you
can authorize and try protected endpoints directly:

- Swagger UI: `http://localhost:8080/swagger-ui.html`
- OpenAPI JSON: `http://localhost:8080/v3/api-docs`

Click **Authorize**, paste the token from `/api/auth/login`, and invoke any endpoint.

---

## Testing

```bash
./mvnw clean verify          # unit + integration tests
./mvnw test                  # unit tests only
```

- **Unit tests** cover truncation, DTO mapping, the price filter, cache-key normalization, the service
  layer (Mockito), JWT round-trips, and the paged-response envelope.
- **Integration tests** cover the source against `MockRestServiceServer` (bound to the `RestClient`),
  the full API over H2 including the auth flow, the RFC-7807 error paths (400/401/403/404/502), and
  cache dedup.
- The Mockito java agent is attached up front by the build (surefire/failsafe) so tests run
  warning-free on JDK 25+.

---

## Project structure

```
src/main/java/com/abysalto/middleware
├── config/       # typed properties, security, cache, OpenAPI, RestClient, seed user
├── controller/   # ProductController, AuthController
├── domain/       # internal domain model (Product, ProductPage, …)
├── dto/          # API DTOs (summary, detail, paged envelope, auth)
├── exception/    # domain exceptions + GlobalExceptionHandler (RFC 7807)
├── model/        # JPA entities (UserAccount, Role)
├── repository/   # Spring Data repositories
├── security/     # JWT service, auth filter, user details
├── service/      # ProductService, ProductQueryCache, ProductMapper
├── source/       # ProductSource abstraction
│   └── dummyjson # DummyJSON implementation + isolated upstream DTOs
├── util/         # TextUtils (truncation)
└── web/          # RequestLoggingFilter (correlation id)
```

---

## AI usage disclosure

This project was built with the assistance of **Claude Code** (Anthropic). AI was used to scaffold the
Spring Boot project, design and implement the `ProductSource` abstraction and DummyJSON adapter, the
DTO/mapping and truncation logic, filtering/search/caching, JWT security, error handling, logging,
OpenAPI setup, the unit and integration test suite, and this documentation and the Docker/Compose
delivery files. All generated code was reviewed and adjusted incrementally, with progress reflected in
the git history. Design intent and rationale are captured in code comments throughout.
