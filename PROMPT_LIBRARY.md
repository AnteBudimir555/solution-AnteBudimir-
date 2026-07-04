# PROMPT_LIBRARY.md

> **Project:** Product Middleware REST API (Abysalto Backend Academy entry task)
> **Purpose:** A chronological, end-to-end prompt library for building this project greenfield with an AI coding assistant.
> **Scope:** Every prompt below is hardcoded to *this* project's stack and domain — a Spring Boot middleware that re-exposes DummyJSON products behind an extensible `ProductSource` abstraction.

## Hardcoded Tech Stack (derived from `TASK.md`)

| Concern | Choice |
|---|---|
| Language | **Java 17** (LTS; task requires ≥ 17, major versions only) |
| Framework | **Spring Boot 3.x** (task requires > 3.0) — Spring Web, Spring Validation |
| Security | **Spring Security 6 + JWT** (token-based; backed by DummyJSON login or local JPA user table) |
| Persistence | **Spring Data JPA / Hibernate**, **H2** (dev/test) with **PostgreSQL** as the prod-capable driver |
| Caching | **Spring Cache abstraction + Caffeine** (TTL + parameter-based keys) |
| HTTP client | **Spring `RestClient`** (Boot 3.2+) for the DummyJSON upstream |
| Build tool | **Maven** |
| API docs | **springdoc-openapi** (Swagger UI at `/swagger-ui.html`) |
| Testing | **JUnit 5**, **Mockito**, **Spring Boot Test**, **MockMvc**, **WireMock** (upstream stubbing) |
| Logging | **SLF4J + Logback**, structured JSON-capable, level-appropriate |
| Containerization | **Docker + docker-compose** (app + PostgreSQL) |
| Upstream source | **DummyJSON** (`https://dummyjson.com`) — products, categories, search, auth |

### Domain invariants every prompt must respect
- **Middleware, not a passthrough:** clients never see raw DummyJSON shapes.
- **Extensible source:** all endpoints depend on a `ProductSource` interface; `DummyJsonProductSource` is one concrete impl. Adding a DB/file/RSS source later must require **zero** controller/service changes.
- **Two DTO shapes:** a *summary* DTO (`image`, `name`, `price`, `shortDescription` ≤ 100 chars) for list/filter/search; a *detail* DTO (full fields) for single-product.
- **Safe truncation:** `shortDescription` is hard-capped at 100 chars, null-safe.
- **Push down where possible:** prefer upstream `limit`/`skip`, `/search?q=`, `/category/{category}`, `?select=`; filter in-service only when upstream can't (document the choice).

### How to use this library
Work the phases **in order**. Each phase gives you: *Context & Purpose* (when to fire it), *The Exact Prompt Text* (copy-paste; fill `{{PLACEHOLDERS}}`), and *Expected Output Format*. Feed the output of one phase as context into the next. Commit after each meaningful step.

---

## Phase 1 — Project Initialization & Architecture Design

### Context & Purpose
Fire this **once, at the very start**, on an empty repository. It produces the Maven project skeleton, the layered package structure, the `ProductSource` abstraction contract, configuration files, and the architectural decisions that every later phase builds on. Do not write feature code yet — this phase decides *where* code will live and *how* sources plug in.

### The Exact Prompt Text
```
You are a Principal Software Architect expert in Java 17, Spring Boot 3.x, Spring Data
JPA/Hibernate, Spring Security, Maven, and REST middleware/gateway design.

Think step-by-step and reason out loud before emitting files. Prioritize scalable design
patterns, security, type safety, and extensibility over quick solutions. Explicitly call out
edge cases and the trade-offs behind every decision.

CONTEXT — the project:
We are building a MIDDLEWARE REST API that re-exposes products from interchangeable upstream
sources. Today the only source is the third-party DummyJSON REST API (https://dummyjson.com),
but the codebase MUST make it trivial to add new source types later (another web service, a
database, the file system, RSS) WITHOUT rewriting any consumer. All endpoints must depend on a
`ProductSource` (a.k.a. `ProductProvider`) abstraction — never on DummyJSON types directly.

Client-facing endpoints that will be built later:
1. List products — trimmed shape (image, name, price, shortDescription ≤ 100 chars), paginated.
2. Product details — full detail of one product by id.
3. Filter products — by category and by price min/max, combinable.
4. Search products by name — free-text query.

Non-functional requirements to design for now (even if implemented later): JWT auth, Spring
Cache + Caffeine caching keyed by normalized request params, structured logging, springdoc
OpenAPI docs, unit + integration tests, Docker/docker-compose, consistent error responses.

YOUR TASK — produce the architecture and skeleton ONLY (no business logic yet):
1. Recommend the exact package layout under `com.abysalto.middleware` using clean layering:
   controller / service / source (provider abstraction + concrete impls) / model / dto /
   mapper / config / security / exception. Justify each package in one line.
2. Define the `ProductSource` interface: the methods needed to satisfy all four endpoints
   (list with pagination, get by id, filter by category+price, search by name), expressed in
   INTERNAL domain terms — not DummyJSON terms. Include a capability/paging contract so a source
   can declare what it can push down vs. what the service must do in-memory.
3. Specify the internal domain model + the two DTO shapes (summary vs. detail) and where the
   mapping layer lives.
4. Produce `pom.xml` with pinned, mutually compatible versions for: Spring Boot 3.x starter-web,
   starter-data-jpa, starter-security, starter-validation, starter-cache, Caffeine, springdoc
   -openapi-starter-webmvc-ui, H2, PostgreSQL driver, Lombok (optional — state if you avoid it),
   plus test deps (starter-test, WireMock, spring-security-test).
5. Produce `application.yml` (+ profile files: `dev` using H2, `prod`/`docker` using PostgreSQL)
   with placeholders for the DummyJSON base URL, JWT secret/expiry, and cache TTL.
6. Produce the Spring `@Configuration` beans that are pure infrastructure: RestClient bean for the
   upstream, CacheManager (Caffeine) bean, and OpenAPI metadata bean.
7. List the Architecture Decision Records (ADRs) as a short bullet list: source abstraction
   strategy, push-down-vs-in-service filtering policy, caching key strategy, and error model.

Do NOT implement endpoint logic, the DummyJSON client body, or entities yet — only interfaces,
config, build files, and the skeleton with clearly marked `// TODO Phase N` stubs.
```

### Expected Output Format
- Present the **package tree** first as a fenced ASCII diagram, with a one-line rationale table beneath it.
- Emit each file in its own fenced block, **prefixed by its exact repo-relative path** as a comment on the first line (e.g. `// src/main/java/com/abysalto/middleware/source/ProductSource.java`).
- `pom.xml` and `application*.yml` as complete, valid, copy-paste-ready files with **pinned versions** (no `LATEST`/`RELEASE`).
- Interfaces fully typed with Javadoc on each method describing contract + edge cases; method bodies that belong to later phases are `throw new UnsupportedOperationException("TODO Phase 3")` or clearly commented stubs.
- End with a **numbered ADR list** and a **"Next steps → Phase 2"** handoff line naming the files Phase 2 must create.

---

## Phase 2 — Database & State Modeling

### Context & Purpose
Fire this after the skeleton compiles. It designs the JPA persistence layer the task calls for — the local **user table** that backs JWT auth, and the **cache/state** structures (whether a persisted cache table or the in-memory Caffeine key model). Even though products are fetched live from DummyJSON, persistence exists for users, auth, and optional local product caching.

### The Exact Prompt Text
```
You are a Senior Database Engineer / Data Architect expert in Spring Data JPA, Hibernate, H2,
and PostgreSQL, designing schemas for a Spring Boot 3.x middleware service.

Think step-by-step. Prioritize normalized, type-safe, migration-friendly schema design, correct
indexing for the query patterns, and security (never store plaintext secrets) over shortcuts.
Enumerate edge cases (nullability, uniqueness, concurrent writes, cache staleness) explicitly.

CONTEXT:
This middleware fetches products live from DummyJSON, so products themselves are NOT the primary
persisted entity. Persistence exists for: (a) local application users backing JWT auth, and
(b) OPTIONAL local product caching / request-dedup state. The service runs on H2 in dev/test and
PostgreSQL in prod via Spring profiles. Entities are consumed by a service layer that sits behind
the `ProductSource` abstraction defined in Phase 1.

YOUR TASK:
1. Design the `User` entity for local auth: id, username (unique), password hash (BCrypt), roles
   (as an enum-backed collection or join table), enabled flag, audit timestamps. Explain the
   role modeling choice (embedded collection vs. join table) and its trade-offs.
2. Design the JPA entity/entities and repositories (`JpaRepository`) with correct annotations,
   `FetchType`, cascade, and unique constraints. Provide derived query methods needed for auth
   (e.g. `findByUsername`).
3. Model the CACHING state: decide and justify whether search/filter caching is (a) purely
   in-memory Caffeine keyed by normalized params (no table), or (b) a persisted `cached_response`
   entity. Recommend (a) for this task and explain why; if any state must persist, model it.
   Define the exact cache key normalization rule (sorted params, lowercased category, rounded
   price bounds, page/size) as a documented contract.
4. Provide DDL-equivalent guidance: which columns get indexes/unique constraints and why, tuned
   to the actual query patterns (login by username, cache lookups).
5. Provide seed data for a test user (username + BCrypt hash) usable to obtain a JWT, and note it
   belongs in a `data.sql`/dev-profile seeder, not prod.
6. State the Hibernate `ddl-auto` policy per profile (create-drop for test, validate/none + real
   migrations for prod) and warn where auto-DDL is demo-only.

Ensure every entity maps cleanly to the internal domain model from Phase 1 and does NOT leak
DummyJSON field names.
```

### Expected Output Format
- One fenced block per Java file, each **prefixed with its repo-relative path comment**; entities, enums, and repositories separated.
- A **schema summary table** per table: column, type, constraints, index, rationale.
- The **cache-key normalization rule** written as an explicit, ordered algorithm (numbered steps) plus one worked example turning request params into a canonical key string.
- `data.sql` (or a `CommandLineRunner` seeder) as a complete block, with the seed user's plaintext test password stated in a comment for README use.
- A short **profile matrix** (dev/test/prod) showing datasource + `ddl-auto` per profile.
- Close with a **"Next steps → Phase 3"** line listing the entities/repos now available to services.

---

## Phase 3 — Core Feature Implementation

### Context & Purpose
The main build phase. Fire it **once per feature slice** (list, detail, filter, search, then auth as its own slice), reusing the same persona and feeding it the Phase 1 interface + Phase 2 entities. It implements the `DummyJsonProductSource`, the mapping/truncation logic, the service layer, the controllers, DTO validation, and the global error handling.

### The Exact Prompt Text
```
You are a Senior Software Engineer expert in Java 17, Spring Boot 3.x, Spring Web, Spring Data
JPA, Spring Security, and clean layered REST design.

Think step-by-step and implement incrementally. Prioritize type safety, clean code, SOLID
boundaries, and exhaustive edge-case handling over quick solutions. Before coding, restate the
contract and list the edge cases you will cover; after coding, note anything deferred.

CONTEXT — reuse, do not redefine:
- The `ProductSource` abstraction and internal domain model / summary+detail DTOs from Phase 1.
- The `User` entity + repositories from Phase 2.
- Upstream is DummyJSON: products `GET /products?limit=&skip=`, detail `GET /products/{id}`,
  search `GET /products/search?q=`, by category `GET /products/category/{category}`,
  categories `GET /products/categories`, field selection `?select=`, auth `POST /auth/login`.
- Rule: clients NEVER see DummyJSON shapes; controllers depend only on the service, which depends
  only on `ProductSource`.

FEATURE SLICE TO IMPLEMENT NOW: {{FEATURE_SLICE}}
(one of: LIST | DETAIL | FILTER_CATEGORY_PRICE | SEARCH_BY_NAME | AUTH_JWT)

YOUR TASK for this slice:
1. Implement / extend `DummyJsonProductSource` for this slice using Spring `RestClient` against
   `{{DUMMYJSON_BASE_URL}}`. Map upstream payloads into the INTERNAL domain model via a dedicated
   mapper — no DummyJSON types past the source boundary.
2. Enforce the trimmed summary shape for list/filter/search: `image`, `name`, `price`,
   `shortDescription`. Implement truncation as a null-safe utility hard-capped at 100 chars
   (document whether you cut mid-word or at a word boundary; at minimum hard-cap).
3. Push filters/search to the upstream where supported (`/search?q=`, `/category/{category}`,
   `limit`/`skip`); when upstream can't combine constraints (e.g. category AND price range),
   filter the remainder in-service and DOCUMENT the fallback in a code comment.
4. Implement the service method(s) and the controller endpoint(s) with proper `@Valid` request
   objects, pagination params, and correct HTTP status codes (200, 400 on bad params, 401/403 for
   auth, 404 for unknown id, 502/504 for upstream failures).
5. For AUTH_JWT: wire Spring Security — a stateless JWT filter, login endpoint issuing a signed
   token (HS256, secret + expiry from config), password hashing with BCrypt, and method/URL
   authorization protecting the product endpoints. Support the DummyJSON login flow OR the local
   user table (state which and why).
6. Add SLF4J logging at appropriate levels (INFO for requests/cache decisions, WARN for upstream
   degradation/fallbacks, ERROR for failures) with no secret/token leakage.
7. Wrap upstream calls with resilient error handling that maps failures to the service's own
   exception types feeding the global handler.

Return production-ready code. Do not stub. Do not break the source abstraction. Do not duplicate
logic already produced in earlier slices — extend it.
```

### Expected Output Format
- One fenced block per file, each **prefixed with its repo-relative path comment**; group by layer (source impl → mapper → service → controller → dto → exception).
- Request/response DTOs as Java `record`s where immutable, fully annotated with `jakarta.validation` constraints; **no field leaks the DummyJSON schema**.
- A **global `@RestControllerAdvice`** producing a consistent error body (`timestamp`, `status`, `error`, `message`, `path`) — emit it in the first slice and reference (don't re-emit) it afterward.
- Each new/changed endpoint documented inline with springdoc annotations (`@Operation`, `@ApiResponse`).
- Precede the code with a **contract + edge-case checklist** (null description, empty search, price min>max, unknown category, upstream 5xx/timeout, page beyond range) and mark each ✅ handled.
- Close with a **"Next steps → Phase 4"** line naming the exact classes/methods now needing tests.

---

## Phase 4 — Automated Test Generation

### Context & Purpose
Fire this after each slice from Phase 3 compiles and runs. It generates the unit + integration tests the task explicitly requires, with the DummyJSON upstream **stubbed via WireMock** so tests are deterministic and offline. Cover the source abstraction, truncation/mapping, filtering, search, and auth.

### The Exact Prompt Text
```
You are a Lead QA Automation Engineer expert in JUnit 5, Mockito, Spring Boot Test, MockMvc,
WireMock, and testing Spring Boot 3.x REST middleware.

Think step-by-step. Prioritize meaningful coverage of edge cases and contracts over line-count or
trivial assertions. Tests must be deterministic, isolated, and runnable offline — never hit the
real DummyJSON. State your test matrix before writing tests.

CONTEXT — code under test:
- `ProductSource` abstraction + `DummyJsonProductSource` (RestClient → DummyJSON).
- Mapper with the 100-char null-safe `shortDescription` truncation.
- Service layer: list (paginated), detail by id, filter (category + price, combinable),
  search by name.
- Spring Security JWT auth over the product endpoints.
- Consistent error model via `@RestControllerAdvice`.

TARGET UNDER TEST NOW: {{TARGET_CLASS_OR_SLICE}}

YOUR TASK:
1. UNIT tests (Mockito, no Spring context) for:
   - The truncation/mapping logic: null description, description < 100, exactly 100, > 100 chars,
     empty string, and that summary DTOs never expose detail-only/DummyJSON fields.
   - The service layer with a mocked `ProductSource`: verify push-down vs. in-service filtering,
     price min>max rejection, unknown category, empty search, pagination math (limit/skip).
2. INTEGRATION tests (`@SpringBootTest` + MockMvc) with the upstream stubbed by WireMock:
   - Each endpoint end-to-end returns correct JSON shape and HTTP status.
   - List returns trimmed shape only; detail returns full shape.
   - Filter combines category + price; search matches by name.
   - Upstream failure (WireMock returns 500/timeout) surfaces as the service's mapped error
     (e.g. 502/504) with the consistent error body.
   - AUTH: unauthenticated request → 401; valid JWT → 200; wrong/expired token → 401/403.
3. Provide reusable WireMock stub fixtures mirroring real DummyJSON payloads (products list,
   single product, search, category), and JSON assertion helpers.
4. Use parameterized tests (`@ParameterizedTest`) for the truncation boundary cases and the
   filter combinations.
5. Ensure tests run under the `test` profile against H2 and require no network.

Do not write assertion-free or tautological tests. Each test name must state the scenario and the
expected outcome.
```

### Expected Output Format
- One fenced block per test file, each **prefixed with its repo-relative path comment** under `src/test/java/...`; unit and integration tests in separate files.
- Begin with a **test matrix table**: scenario → input → expected output/status → test type (unit/integration).
- WireMock stubs and JSON fixtures emitted as separate files under `src/test/resources/...` (referenced, not inlined into every test).
- Test method names in `should_<expected>_when_<condition>` form; use `@DisplayName` for readable output.
- Parameterized boundary cases presented with an explicit `@MethodSource`/`@CsvSource` data table.
- Close with a **coverage note**: which task acceptance-checklist items each test file satisfies, and any gap deferred to Phase 5.

---

## Phase 5 — Code Review, QA & Optimization

### Context & Purpose
Fire this **before considering the project done / before the final commit**. It's an adversarial review pass: a strict reviewer hunts for security holes (JWT, secret handling), performance issues (upstream N+1, missing cache hits), abstraction leaks (DummyJSON bleeding through), and clean-code / task-compliance gaps against the acceptance checklist.

### The Exact Prompt Text
```
You are a Strict Technical Reviewer / Staff Engineer specializing in Java 17, Spring Boot 3.x
security, performance, and clean architecture, reviewing a product middleware before release.

Think step-by-step and be uncompromising. Assume the code is guilty until proven correct.
Prioritize security vulnerabilities, correctness under edge cases, performance, and architectural
integrity over politeness. Every finding must be concrete and actionable — cite the file/method,
explain the risk, and give the fix.

CONTEXT — what "correct" means for this project:
- Middleware over DummyJSON behind an extensible `ProductSource`; adding a new source must need
  ZERO controller/service changes.
- Summary shape = image/name/price/shortDescription(≤100, null-safe); detail = full.
- JWT auth, Spring Cache + Caffeine keyed by normalized params, structured logging, springdoc
  docs, unit + integration tests, consistent error responses, Docker/docker-compose.

CODE TO REVIEW:
{{CODE_OR_DIFF_OR_MODULE}}

REVIEW CHECKLIST — report findings under each heading, most severe first:
1. SECURITY: JWT signing/validation (algorithm confusion, weak/hardcoded secret, missing expiry
   check), BCrypt usage, secrets in logs/responses/config, missing authz on endpoints, input
   validation gaps, SSRF/injection risk in the upstream call, verbose error leakage.
2. ABSTRACTION INTEGRITY: any DummyJSON type/field/URL leaking past the `ProductSource` boundary;
   controllers/services coupled to the concrete source; whether a second source could truly be
   added without edits — prove or disprove.
3. CORRECTNESS & EDGE CASES: truncation boundary (100/empty/null), price min>max, unknown
   category, empty search, pagination bounds, combinable filters, upstream 4xx/5xx/timeout mapping.
4. PERFORMANCE: redundant upstream calls, cache misses on equivalent params (key normalization
   bugs), TTL correctness, blocking/timeout config, unnecessary full-list fetches when upstream
   could filter.
5. CLEAN CODE & CONVENTIONS: layering violations, naming, dead code, missing/incorrect HTTP
   status codes, error-response consistency, logging levels, Javadoc/OpenAPI completeness.
6. TASK COMPLIANCE: walk the acceptance checklist (list/detail/filter/search, abstraction, auth,
   caching, logging, tests, OpenAPI, README, incremental git history, AI-usage disclosure) and
   mark each PASS / FAIL / PARTIAL with evidence.

For each finding: Severity (Critical/High/Medium/Low) · File:method · Risk · Recommended fix
(with a corrected code snippet where useful).
```

### Expected Output Format
- Lead with a **verdict line** (Ship / Ship-with-fixes / Block) and a one-paragraph summary.
- A **findings table**: `#`, Severity, Location (file:method), Category, Risk, Fix.
- Group detailed findings by the six checklist headings, **most severe first**; include corrected code snippets in fenced blocks with the repo-relative path comment.
- A final **Acceptance Checklist audit** table: each `TASK.md` item → PASS / FAIL / PARTIAL → evidence/line reference.
- End with a prioritized, numbered **remediation plan** (do-first ordering) and a note on which fixes need a new test in Phase 4.

---

## Appendix — Cross-Cutting Micro-Prompts

Small, reusable prompts for recurring greenfield chores. Same persona discipline applies.

### A1 · README authoring
```
You are a Senior Developer Advocate expert in Spring Boot 3.x and Docker documentation.
Think step-by-step. Write a README.md for this DummyJSON product-middleware that covers: one-line
what/why, tech stack, prerequisites (Java 17, Maven, Docker), local run (Maven + docker-compose
with PostgreSQL), profile/config table (DummyJSON base URL, JWT secret/expiry, cache TTL), how to
obtain a JWT with the seeded test user (state username + password), the full endpoint reference
(list/detail/filter/search) with example curl calls and responses, Swagger UI URL, how to run
unit + integration tests, and an AI-usage disclosure section (where/why AI was used; link this
PROMPT_LIBRARY.md). Output as a single copy-paste README.md.
```

### A2 · Dockerfile + docker-compose
```
You are a Senior DevOps Engineer expert in containerizing Spring Boot 3.x apps.
Think step-by-step; prioritize small, secure images and reproducible one-command startup. Produce
a multi-stage Dockerfile (Maven build → slim JRE 17 runtime, non-root user) and a docker-compose
.yml wiring the app to a PostgreSQL service with healthchecks, the `docker` Spring profile, and
env-var injection for the DummyJSON URL, JWT secret, and DB creds. Output each file with its path.
```

### A3 · Conventional-commit message
```
You are a Senior Engineer who writes precise Conventional Commits.
Given this diff: {{DIFF}} — produce a single conventional commit (type(scope): subject + body
explaining the what/why) reflecting one incremental step, matching the task's requirement for a
readable, incremental commit history. Output only the commit message.
```

### A4 · Debugging an upstream/integration failure
```
You are a Senior Software Engineer expert in Spring Boot 3.x, RestClient, and WireMock debugging.
Think step-by-step from symptom to root cause. Given this error/log: {{ERROR_LOG}} and this code
context: {{CODE_CONTEXT}} — form hypotheses ranked by likelihood, identify the root cause without
guessing, and give the minimal correct fix plus a regression test that would have caught it. Keep
the `ProductSource` abstraction intact.
```
