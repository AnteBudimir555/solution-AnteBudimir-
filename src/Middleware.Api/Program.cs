using Middleware.Api.Endpoints;
using Middleware.Api.Errors;
using Middleware.Api.Middleware;
using Middleware.Api.OpenApi;
using Middleware.Api.Security;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Middleware.Api.Serialization;
using Middleware.Core.Options;
using Middleware.Core.Services;
using Middleware.Infrastructure.Persistence;
using Middleware.Infrastructure.Security;
using Middleware.Infrastructure.Upstream;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// --- logging -----------------------------------------------------------------
// Serilog's LogContext is the SLF4J MDC analog (see CorrelationIdMiddleware). Development gets the
// readable console pattern including the correlation id; other environments emit compact JSON, the
// replacement for the logstash encoder used by the Java `postgres` profile.
builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();

    if (context.HostingEnvironment.IsDevelopment())
    {
        configuration.WriteTo.Console(outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u5} [{correlationId}] {SourceContext} - {Message:lj}{NewLine}{Exception}");
    }
    else
    {
        configuration.WriteTo.Console(new CompactJsonFormatter());
    }
});

// --- services ----------------------------------------------------------------
// Registration order is load-bearing for the two startup tasks these modules contribute: persistence
// creates the schema, security then seeds the user into it, and hosted services start in the order
// they were registered.
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddUpstreamSource(builder.Configuration);
builder.Services.AddSecurityServices(builder.Configuration);
builder.Services.AddApiSecurity();
builder.Services.AddApiDocumentation(builder.Configuration);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

// Jackson renders every Double with a decimal point ("4.0"); System.Text.Json writes "4". Same value,
// different bytes — and a client diffing raw payloads would see it, so the responses are aligned.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JavaDoubleConverter()));

// A body the framework cannot bind must surface as an exception, so the problem handler renders it
// like every other failure. This defaults to true only in Development, which would make an unreadable
// body answer differently in production than in dev — and differently from the Java service, whose
// HttpMessageNotReadableException always reaches its handler.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

// --- CORS ---------------------------------------------------------------------
// An allowlist, defaulting to empty: no browser origin may read a response unless it is named in
// configuration. Every entry is validated at start-up (see CorsPolicyOptions) because each way of
// misspelling an origin — trailing slash, a path, a non-http scheme — fails silently at runtime.
builder.Services.AddOptions<CorsPolicyOptions>()
    .Bind(builder.Configuration.GetSection(CorsPolicyOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddCors();

// The policy is built from the bound options rather than from a `builder.Configuration.Get<>()` read
// here, and that is load-bearing: top-level statements run before the host is built, so an eager read
// sees only the sources registered by this point and silently ignores any added later — which is how
// the integration suite supplies its origins, and how an operator's extra provider would too. The
// symptom is not an error but an empty allowlist. Same `.Configure<TDep>` shape AddApiSecurity uses to
// feed JwtBearerOptions from JwtService.
builder.Services.AddOptions<CorsOptions>().Configure<IOptions<CorsPolicyOptions>>((cors, allowed) =>
    cors.AddDefaultPolicy(policy => policy
        .WithOrigins(allowed.Value.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));
// No AllowCredentials above, deliberately and permanently. AllowAnyOrigin could not express it — the
// framework throws on that combination — so switching to WithOrigins is precisely the moment it
// becomes expressible, and it is the reflexive addition when a browser client misbehaves. This API
// authenticates by bearer token and needs no credentialed request; enabling it would newly expose
// endpoints that have never had to consider cookie-driven CSRF. CorsTests.CredentialsAreNeverAllowed
// is the guardrail that AllowAnyOrigin used to provide for free.

var app = builder.Build();

// --- pipeline ----------------------------------------------------------------
// First, so the correlation id exists before anything else can log or fail.
app.UseMiddleware<CorrelationIdMiddleware>();
// ProblemDetailsExceptionHandler logs each failure at its own severity, so the middleware's blanket
// "unhandled exception" error line is silenced by a Serilog override (see appsettings.json) rather
// than double-reporting every expected 4xx.
app.UseExceptionHandler();
// Renders the API's problem body for statuses produced without one (unknown route, unsupported
// method or media type, …) — the analog of the Java handler's ProblemDetail "enrich" path.
app.UseStatusCodePages(context =>
    ApiProblem.WriteAsync(context.HttpContext, ApiProblem.ForStatus(context.HttpContext, context.HttpContext.Response.StatusCode)));

// Ahead of routing, so the matcher never gets to normalize the slash away.
app.UseTrailingSlashRejection();

// Documentation is public: served before authentication so no token is required to read it.
app.UseApiDocumentation();

// Explicit, so the CORS and method-mismatch steps below can sit between route resolution and
// authentication.
app.UseRouting();

// Ahead of UsePublicPathMethodMismatch, and that ordering is the whole reason a browser can reach the
// login endpoint at all. A cross-origin POST of JSON is preflighted, and OPTIONS matches no route on
// /api/auth/login, so routing selects the framework's 405 short-circuit — which the method-mismatch
// step then *runs*, answering 405 before CORS is consulted. The preflight fails, so the browser never
// sends the real POST. CORS must get first refusal: it recognizes the preflight and answers it. It
// still sits after UseRouting so endpoint metadata is available, and before UseAuthorization.
app.UseCors();
app.UsePublicPathMethodMismatch();

app.UseAuthentication();
app.UseAuthorization();

app.MapProductEndpoints();
app.MapAuthEndpoints();

// Deliberately no catch-all route: an unmatched path resolves to no endpoint, so the authorization
// policy cannot apply and it is reported as 404 (with the API's problem body, via UseStatusCodePages).
// The Java service answers 401 there instead, because its security filter chain runs ahead of route
// resolution. Adding a catch-all would restore that, at the cost of turning every method mismatch on a
// known path into a 404 — standard HTTP semantics are worth more than parity on an unrouted path.

app.Run();

/// <summary>
/// Entry-point marker made public so the integration tests can host the real pipeline via
/// <c>WebApplicationFactory&lt;Program&gt;</c> (the analog of Spring's <c>@SpringBootTest</c>).
/// </summary>
public partial class Program;
