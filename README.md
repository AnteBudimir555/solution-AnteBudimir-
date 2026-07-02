# Product Middleware REST API

Middleware REST API koji ponovno izlaže proizvode iz izvora treće strane (trenutno
[DummyJSON](https://dummyjson.com)) kroz čisti, filtrirani, JWT-zaštićeni API.

Servis se nalazi **ispred** izvornog (upstream) izvora proizvoda i klijentima izlaže osviježeni, keširani
API proizvoda. Do upstream izvora se pristupa isključivo kroz apstrakciju `ProductSource`, pa se kasnije
mogu dodati dodatni tipovi izvora (drugi web servisi, baza podataka, datotečni sustav, RSS, …) bez
diranja kontrolera, servisa ili DTO-ova — DummyJSON je samo jedna konkretna implementacija.

---

## Sadržaj

- [Tehnološki stog](#tehnološki-stog)
- [Arhitektura](#arhitektura)
- [Endpointi](#endpointi)
- [Pokretanje lokalno](#pokretanje-lokalno)
  - [Dev profil (H2, bez postavljanja)](#1-dev-profil-h2-bez-postavljanja)
  - [Docker Compose (app + Postgres)](#2-docker-compose-app--postgres)
  - [Postgres profil bez Dockera](#3-postgres-profil-bez-dockera)
- [Autentifikacija i testni korisnik](#autentifikacija-i-testni-korisnik)
- [Referenca konfiguracije](#referenca-konfiguracije)
- [Keširanje / deduplikacija zahtjeva](#keširanje--deduplikacija-zahtjeva)
- [Filtriranje i pretraga (push-down)](#filtriranje-i-pretraga-push-down)
- [Rukovanje pogreškama](#rukovanje-pogreškama)
- [Logiranje](#logiranje)
- [API dokumentacija (Swagger)](#api-dokumentacija-swagger)
- [Testiranje](#testiranje)
- [Struktura projekta](#struktura-projekta)
- [Napomena o korištenju AI-ja](#napomena-o-korištenju-ai-ja)

---

## Tehnološki stog

| Područje            | Odabir                                                             |
|--------------------|--------------------------------------------------------------------|
| Jezik / runtime | Java 21 (razina izdanja), izgrađeno na JDK 25                           |
| Framework          | Spring Boot 4.1.0 (Spring Web MVC, sinkroni `RestClient`)       |
| Alat za izgradnju         | Maven (putem priloženog `./mvnw` wrappera)                           |
| Perzistencija        | Spring Data JPA / Hibernate — H2 (dev/test), PostgreSQL (Docker)   |
| Sigurnost           | Spring Security, bezstanjski JWT (jjwt), BCrypt hashiranje lozinki     |
| Keširanje            | Spring Cache s Caffeine pozadinom                                    |
| API dokumentacija           | springdoc-openapi (Swagger UI)                                     |
| Logiranje            | SLF4J / Logback, MDC korelacijski id, JSON logovi u prod profilu |
| Testovi              | JUnit 5, Mockito, `MockRestServiceServer`, `MockMvc`              |

> **Zašto Maven:** projekt koristi Maven wrapper (`./mvnw`), pa nije potrebna lokalna instalacija Mavena.

---

## Arhitektura

```
Client ─▶ ProductController ─▶ ProductService ─▶ ProductQueryCache ─▶ ProductSource (interface)
                                    │                (Caffeine)              │
                                    └─ ProductMapper (domain → DTO)          └─ DummyJsonProductSource
                                                                                (RestClient → DummyJSON)
```

- **`ProductSource`** (`source/ProductSource.java`) — točka proširenja. Vraća interne **domenske**
  tipove (`Product`, `ProductPage`), nikada upstream tipove.
- **`DummyJsonProductSource`** — jedina trenutna implementacija. Upstream JSON je izoliran u
  `source/dummyjson/dto/*` i mapiran u domenu pomoću `DummyProductMapper`.
- **`ProductService`** — orkestracija: paginacija, mapiranje DTO-ova i in-service filter cijene.
- **`ProductQueryCache`** — zaseban bean koji sadrži `@Cacheable` metode (kako bi se poštovao Springov
  cache proxy i ne bi ga se zaobišlo samopozivom).
- **DTO-ovi** — osviježeni `ProductSummaryDto` (lista/filter/pretraga) i puni `ProductDetailDto` (detalj).

---

## Endpointi

Svi odgovori su u JSON formatu. Svaki `/api/products/**` endpoint zahtijeva zaglavlje
`Authorization: Bearer <token>`; `/api/auth/login` i Swagger dokumentacija su javno dostupni.

| Metoda | Putanja                       | Opis                                             | Auth |
|--------|----------------------------|---------------------------------------------------------|:----:|
| POST   | `/api/auth/login`          | Zamjena korisničkog imena/lozinke za JWT                    |  —   |
| GET    | `/api/products`            | Paginirana, osviježena lista proizvoda                             |  ✔   |
| GET    | `/api/products/{id}`       | Puni detalj pojedinog proizvoda                         |  ✔   |
| GET    | `/api/products/filter`     | Filtriranje po `category` i/ili `minPrice`/`maxPrice`       |  ✔   |
| GET    | `/api/products/search`     | Slobodna tekstualna pretraga po nazivu proizvoda (`q`)                | ✔   |
| GET    | `/api/products/categories` | Dostupni identifikatori kategorija                          |  ✔   |

**Uobičajeni query parametri** (lista/filter/pretraga): `page` (indeksirano od 0, zadano `0`), `size` (1–100, zadano `20`).
Osviježeni oblik je `{ image, name, price, shortDescription }` gdje je `shortDescription` čvrsto ograničen
na 100 znakova, na granici riječi.

### Primjer sesije

```bash
# 1. Prijava (dev seed korisnik) i preuzimanje tokena
TOKEN=$(curl -s -X POST http://localhost:8080/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"demo","password":"demo1234"}' | jq -r .token)

# 2. Osviježena, paginirana lista
curl -s http://localhost:8080/api/products?page=0\&size=5 \
  -H "Authorization: Bearer $TOKEN"

# 3. Puni detalj proizvoda
curl -s http://localhost:8080/api/products/1 -H "Authorization: Bearer $TOKEN"

# 4. Filter: kategorija + raspon cijena (kombinabilno)
curl -s "http://localhost:8080/api/products/filter?category=smartphones&minPrice=100&maxPrice=1000" \
  -H "Authorization: Bearer $TOKEN"

# 5. Pretraga po nazivu
curl -s "http://localhost:8080/api/products/search?q=phone" -H "Authorization: Bearer $TOKEN"
```

---

## Pokretanje lokalno

**Preduvjeti:** JDK 21+ (preporučen JDK 25 radi usklađenosti s toolchainom). Docker je potreban samo za
Compose put. Maven wrapper (`./mvnw` / `mvnw.cmd`) sam upravlja Mavenom.

### 1. Dev profil (H2, bez postavljanja)

Zadani profil. Ugrađeni in-memory H2, omogućena H2 konzola i unaprijed posijani `demo` korisnik — nisu
potrebni vanjski servisi niti dodatna konfiguracija.

```bash
./mvnw spring-boot:run
```

- Aplikacija: `http://localhost:8080`
- Swagger UI: `http://localhost:8080/swagger-ui.html`
- H2 konzola: `http://localhost:8080/h2-console` (JDBC URL `jdbc:h2:mem:middleware`, korisnik `sa`, bez lozinke)
- Seed korisnik: **`demo` / `demo1234`**

### 2. Docker Compose (app + Postgres)

Izgrađuje sliku i pokreće aplikaciju pod `postgres` profilom nasuprot Postgres kontejneru.

```bash
cp .env.example .env      # zatim uredi JWT_SECRET za bilo što ozbiljno
docker compose up --build
```

Compose automatski učitava `.env` (kopiran iz commitanog `.env.example`) za `DB_USERNAME`,
`DB_PASSWORD` i `JWT_SECRET`. `postgres` profil **nema zadane vrijednosti** za njih i brzo puca ako
ijedna nedostaje — a `docker-compose.yml` odražava to isto za `JWT_SECRET`, tako da poznati ključ za
potpisivanje nikada nije ugrađen u praćenu datoteku. Vrijednosti u `.env.example` su jednokratne demo
tajne; generirajte pravu za bilo koje stvarno okruženje (`openssl rand -base64 48`). Također možete
prepisati vrijednost po pozivu iz ljuske:

```bash
JWT_SECRET='a-strong-secret-at-least-32-bytes-long' docker compose up --build
```

Za Compose demo, sijanje (seeding) je eksplicitno omogućeno (`SEED_USER_ENABLED=true`) kako bi
zaštićeni API bio odmah testabilan pomoću **`demo` / `demo1234`**. U stvarnom okruženju sijanje treba
ostaviti isključenim i korisnicima upravljati izvan aplikacije.

### 3. Postgres profil bez Dockera

Usmjerite aplikaciju na vlastiti Postgres i postavite potrebne varijable okruženja:

```bash
export SPRING_PROFILES_ACTIVE=postgres
export DB_URL='jdbc:postgresql://localhost:5432/middleware'
export DB_USERNAME='middleware'
export DB_PASSWORD='middleware'
export JWT_SECRET='a-strong-secret-at-least-32-bytes-long'
# Opcionalno: uključi seed korisnika za testiranje
export SEED_USER_ENABLED=true SEED_USER_USERNAME=demo SEED_USER_PASSWORD=demo1234
./mvnw spring-boot:run
```

---

## Autentifikacija i testni korisnik

Autentifikacija je **temeljena na tokenu (JWT)** s lokalnom JPA tablicom korisnika (`UserAccount`),
BCrypt-hashiranim lozinkama i bezstanjskim Spring Security lancem filtara.

1. **Dohvat tokena** — `POST /api/auth/login` s `{"username": "...", "password": "..."}`.
   Odgovor je `{ "token", "tokenType": "Bearer", "expiresInSeconds" }`.
2. **Poziv zaštićenih endpointa** — pošaljite `Authorization: Bearer <token>`.

**Testni korisnik:** i dev profil i Docker Compose demo posijeku **`demo` / `demo1234`**. Pogrešne
vjerodajnice vraćaju `401`; nedostajući/istekao/nevažeći token na zaštićenom endpointu vraća `401`.

> JWT tajna se nikada ne isporučuje sa zadanom vrijednošću. Mora biti dostavljena putem `JWT_SECRET`
> (ili jednokratne vrijednosti dev profila) i mora imati **≥ 32 znaka** za HS256, inače aplikacija brzo
> puca pri pokretanju.

---

## Referenca konfiguracije

Konfiguracija je vezana na tipizirana `@ConfigurationProperties` svojstva i vođena varijablama okruženja.

| Env varijabla                    | Profil(i)     | Zadano (baza)                     | Svrha                                        |
|----------------------------|----------------|------------------------------------|------------------------------------------------|
| `SPRING_PROFILES_ACTIVE`   | svi            | `dev`                              | Aktivni profil (`dev` / `postgres` / `test`)   |
| `SERVER_PORT`              | svi            | `8080`                             | HTTP port                                      |
| `JWT_SECRET`               | svi            | — (dev ima jednokratnu vrijednost)      | HS256 ključ za potpisivanje (**≥ 32 znaka**, obavezno)   |
| `JWT_EXPIRATION_MINUTES`   | svi            | `60`                               | Vijek trajanja tokena                                  |
| `DUMMYJSON_BASE_URL`       | svi            | `https://dummyjson.com`            | Bazni URL upstream izvora                       |
| `DUMMYJSON_CONNECT_TIMEOUT_MS` | svi        | `3000`                             | Timeout spajanja na upstream                       |
| `DUMMYJSON_RESPONSE_TIMEOUT_MS`| svi        | `5000`                             | Timeout odgovora upstreama                       |
| `DB_URL`                   | postgres       | `jdbc:postgresql://localhost:5432/middleware` | JDBC URL                            |
| `DB_USERNAME`              | postgres       | — (obavezno)                       | Korisnik baze                                        |
| `DB_PASSWORD`              | postgres       | — (obavezno)                       | Lozinka baze                                        |
| `SEED_USER_ENABLED`        | svi            | `false` (`true` u devu)            | Kreiranje seed korisnika pri pokretanju                |
| `SEED_USER_USERNAME`       | svi            | — (`demo` u devu)                  | Ime seed korisnika                                        |
| `SEED_USER_PASSWORD`       | svi            | — (`demo1234` u devu)              | Lozinka seed korisnika                             |

Profili:
- **`dev`** — H2 in-memory, uključena H2 konzola, uključen seed korisnik (`demo`/`demo1234`), jednokratna
  JWT tajna, čitljivi konzolni logovi. Zadano.
- **`postgres`** — nalik produkciji: Postgres, JSON logovi, bez zadanih vrijednosti za DB vjerodajnice /
  JWT tajnu (brzo puca), sijanje isključeno osim ako eksplicitno omogućeno.
- **`test`** — H2, koristi se za automatizirane testove.

> **Upravljanje shemom:** `postgres` profil koristi Hibernate `ddl-auto: update` kako bi demo bio
> samodostatan. Stvarno okruženje trebalo bi postaviti `ddl-auto: validate` i shemom upravljati putem
> verzioniranih migracija (Flyway ili Liquibase), umjesto da Hibernate mutira shemu pri pokretanju.

---

## Keširanje / deduplikacija zahtjeva

Ponovljeni pozivi **pretrage** i **filtriranja** s istim parametrima poslužuju se iz in-memory
**Caffeine** keša (Spring Cache apstrakcija) umjesto ponovnog pogađanja upstreama.

- **Backend / TTL:** Caffeine, `maximumSize=500, expireAfterWrite=60s` (`middleware.cache.spec`).
- **Normalizirani ključevi:** ključeve keša izrađuje `CacheKeys`, tako da se semantički identični
  zahtjevi svode na jedan zapis — tekst se trimira/pretvara u mala slova, a numeričke skale cijena se
  normaliziraju (`10` == `10.00`). Ista normalizacija hrani i ključ keša i stvarni upstream poziv, pa
  nikada ne mogu divergirati.
- **Keš neovisan o paginaciji za filtere:** za upite filtrirane po cijeni cijeli skup kandidata kešira se
  po *kategoriji + granicama cijene* (ne po stranici), tako da listanje kroz filtrirani rezultat ponovno
  koristi jedan upstream dohvat umjesto ponovnog dohvaćanja po stranici.
- **Ispravnost:** `@Cacheable` metode nalaze se u zasebnom `ProductQueryCache` beanu kako bi se Springov
  cache proxy uvijek primijenio (samopoziv iz `ProductService` bi ga inače zaobišao).

---

## Filtriranje i pretraga (push-down)

Filteri se prosljeđuju na upstream tamo gdje ih DummyJSON podržava, a inače se primjenjuju in-service
(dokumentirano u `DummyJsonProductSource`):

| Mogućnost     | Gdje se izvršava | Korišteni upstream                          |
|----------------|---------------|-----------------------------------------|
| Paginacija     | Upstream      | `?limit=&skip=`                         |
| Pretraga po nazivu    | Upstream      | `/products/search?q=`                   |
| Filter kategorije| Upstream      | `/products/category/{slug}`             |
| Raspon cijena    | In-service    | nije podržano na upstreamu — filtrira se lokalno |

Filteri kategorije i cijene su **kombinabilni**: kategorija se prosljeđuje upstreamu, a zatim se raspon
cijena primjenjuje na vraćeni skup kandidata. Zaštita brojanjem kandidata bilježi upozorenje ako se u
memoriji materijalizira neuobičajeno velik skup (sigurno za DummyJSONov mali katalog; veći izvor trebao
bi umjesto toga proslijediti filter cijene upstreamu).

---

## Rukovanje pogreškama

Sve pogreške vraćaju se u obliku **RFC 7807 `application/problem+json`** kroz jedinstveni
`GlobalExceptionHandler`. Sigurnosni neuspjesi (401/403) događaju se unutar lanca filtara, ali se
delegiraju istom handleru, tako da svaka pogreška dijeli isti dosljedan oblik.

| Situacija                                  | Status |
|--------------------------------------------|:------:|
| Validacija / neispravni parametri              | `400`  |
| Nedostajući/nevažeći/istekao token, pogrešna prijava   | `401`  |
| Autentificiran, ali bez ovlasti            | `403`  |
| Nepoznati proizvod / ruta                    | `404`  |
| Neuspjeh ili timeout upstream izvora         | `502`  |
| Neočekivana pogreška poslužitelja                    | `500`  |

---

## Logiranje

Strukturirano SLF4J/Logback logiranje na odgovarajućim razinama (INFO/WARN/ERROR):

- `RequestLoggingFilter` dodjeljuje **korelacijski id** po zahtjevu (MDC) i logira metodu, putanju,
  status i trajanje.
- **Ne-produkcijski profili** koriste čitljiv konzolni format koji uključuje korelacijski id.
- **`postgres` profil** ispisuje jedan **JSON** objekt po liniji (Logstash encoder) s poljima
  `correlationId` / `method` / `path` / `status` / `durationMs` kao zasebnim poljima za agregaciju.
- Tajne se nikada ne logiraju — tijek autentifikacije logira samo korisničko ime, nikada lozinku ili token.

---

## API dokumentacija (Swagger)

springdoc-openapi generira OpenAPI specifikaciju i Swagger UI, s ugrađenom JWT bearer shemom kako biste
mogli autorizirati i isprobati zaštićene endpointe izravno:

- Swagger UI: `http://localhost:8080/swagger-ui.html`
- OpenAPI JSON: `http://localhost:8080/v3/api-docs`

Kliknite **Authorize**, zalijepite token dobiven iz `/api/auth/login`, i pozovite bilo koji endpoint.

---

## Testiranje

```bash
./mvnw clean verify          # unit + integracijski testovi
./mvnw test                  # samo unit testovi
```

- **Unit testovi** pokrivaju skraćivanje teksta, mapiranje DTO-ova, filter cijene, normalizaciju ključeva
  keša, servisni sloj (Mockito), JWT round-trip i omotač paginiranog odgovora.
- **Integracijski testovi** pokrivaju izvor nasuprot `MockRestServiceServer`-u (vezanom na `RestClient`),
  puni API preko H2 uključujući tijek autentifikacije, RFC-7807 putanje pogrešaka (400/401/403/404/502) i
  deduplikaciju keša.
- Mockito java agent se unaprijed prilaže od strane builda (surefire/failsafe) kako bi testovi bili bez
  upozorenja na JDK 25+.

---

## Struktura projekta

```
src/main/java/com/abysalto/middleware
├── config/       # tipizirana svojstva, security, cache, OpenAPI, RestClient, seed korisnik
├── controller/   # ProductController, AuthController
├── domain/       # interni domenski model (Product, ProductPage, …)
├── dto/          # API DTO-ovi (summary, detail, paginirani omotač, auth)
├── exception/    # domenske iznimke + GlobalExceptionHandler (RFC 7807)
├── model/        # JPA entiteti (UserAccount, Role)
├── repository/   # Spring Data repozitoriji
├── security/     # JWT servis, auth filter, user details
├── service/      # ProductService, ProductQueryCache, ProductMapper
├── source/       # ProductSource apstrakcija
│   └── dummyjson # DummyJSON implementacija + izolirani upstream DTO-ovi
├── util/         # TextUtils (skraćivanje)
└── web/          # RequestLoggingFilter (korelacijski id)
```

---

## Napomena o korištenju AI-ja

Ovaj projekt izrađen je uz pomoć **Claude Codea** (Anthropic). Sav generirani kod je pregledan i prilagođen
postupno, a napredak je zabilježen u git povijesti.
