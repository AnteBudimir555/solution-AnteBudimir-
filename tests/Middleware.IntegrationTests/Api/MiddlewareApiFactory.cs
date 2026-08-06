using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Middleware.Core.Abstractions;
using Middleware.Core.Domain;
using Middleware.Infrastructure.Persistence;
using Middleware.Infrastructure.Security;
using NSubstitute;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Middleware.IntegrationTests.Api;

/// <summary>
/// Hosts the real API pipeline in-process — the analog of <c>@SpringBootTest</c> +
/// <c>@AutoConfigureMockMvc</c> in the Java suite. Only the network boundary
/// (<see cref="IProductSource"/>) is substituted, so the JWT auth flow, the RFC-7807 error contract and
/// request validation are all exercised as a client would hit them.
///
/// <para>The user store is a private in-memory SQLite database, held open for the factory's lifetime
/// (the connection <em>is</em> the database), replacing the Java suite's in-memory H2.</para>
/// </summary>
public sealed class MiddlewareApiFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret = "test-secret-key-that-is-sufficiently-long-for-hs256-signing-0123456789";
    public const string JwtIssuer = "abysalto-middleware";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private string _jwtSecret = JwtSecret;
    private (string Username, string Password)? _seedUser;

    public MiddlewareApiFactory() => _connection.Open();

    /// <summary>The substituted upstream; configure it per test, as the Java ITs do with a mock bean.</summary>
    public IProductSource Source { get; } = Substitute.For<IProductSource>();

    /// <summary>Log events emitted by the running host, for assertions on the logging contract.</summary>
    public CapturedLogEvents Logs { get; } = new();

    /// <summary>Overrides the signing secret before the host is built (for the fail-fast tests).</summary>
    public MiddlewareApiFactory WithJwtSecret(string secret)
    {
        _jwtSecret = secret;
        return this;
    }

    /// <summary>Enables the startup seeder before the host is built.</summary>
    public MiddlewareApiFactory WithSeedUser(string username, string password)
    {
        _seedUser = (username, password);
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Staging);

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = _jwtSecret,
                ["Jwt:Issuer"] = JwtIssuer,
                ["Jwt:ExpirationMinutes"] = "60",
                // Tests create their own fixtures, so the seeder is off unless a test asks for it.
                ["Security:SeedUser:Enabled"] = _seedUser is null ? "false" : "true",
                ["Security:SeedUser:Username"] = _seedUser?.Username,
                ["Security:SeedUser:Password"] = _seedUser?.Password
            }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll<IProductSource>();
            services.AddSingleton(Source);

            // Program.cs calls ReadFrom.Services(...), which applies every ILoggerSettings registered
            // in DI, so this sink joins the real logging pipeline and sees exactly what the
            // application writes.
            services.AddSingleton<ILoggerSettings>(Logs);
        });
    }

    /// <summary>
    /// Empties every cache entry in the running host — the analog of the Java suite's
    /// <c>clearCaches</c>, and a prerequisite for any test that stubs a failure on an endpoint whose
    /// results are cached: a warm entry from an earlier test is served without the substitute ever
    /// being consulted, so the test passes or fails on execution order.
    ///
    /// <para><c>"*"</c> is HybridCache's wildcard tag. Measured on 10.8.0: it evicts entries written
    /// with no tags at all, which is what makes this a true clear-all and lets these suites drop the
    /// hand-maintained key lists they used to carry.</para>
    /// </summary>
    public async Task ClearCachesAsync() =>
        await Services.GetRequiredService<HybridCache>().RemoveByTagAsync("*");

    /// <summary>Clears the user store and inserts a single account with the given credentials.</summary>
    public async Task ResetUsersAsync(string username, string password)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        db.Users.RemoveRange(db.Users);
        await db.SaveChangesAsync();

        db.Users.Add(new UserAccount(username, hasher.Hash(password), Role.User));
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}

/// <summary>In-memory Serilog sink recording everything the host logs, for logging-contract assertions.</summary>
public sealed class CapturedLogEvents : ILogEventSink, ILoggerSettings
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEvent> _events = new();

    public void Configure(LoggerConfiguration loggerConfiguration) => loggerConfiguration.WriteTo.Sink(this);

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public IReadOnlyList<LogEvent> Snapshot() => [.. _events];

    public void Clear() => _events.Clear();
}
