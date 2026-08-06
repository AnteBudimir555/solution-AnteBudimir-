using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Middleware.Core.Options;
using Middleware.Core.Services;
using Middleware.Infrastructure.Security;

namespace Middleware.IntegrationTests.Api;

/// <summary>
/// Host composition tests — the analog of <c>MiddlewareApplicationTests.contextLoads</c>, plus the
/// JWT fail-fast that Phase 1 deferred until the API container existed.
/// </summary>
public sealed class ApplicationStartupTests
{
    [Fact]
    public void HostStartsAndResolvesTheApplicationGraph()
    {
        using var factory = new MiddlewareApiFactory();

        // Forces the host to build and every hosted service (schema init, seeder) to start.
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProductService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<JwtService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPasswordHasher>());
    }

    [Fact]
    public async Task SeededUserCanAuthenticateAgainstTheFreshlyCreatedSchema()
    {
        // Covers the startup ordering contract: the schema initializer must run before the seeder,
        // and the seeder must store a hash the login endpoint can verify.
        using var factory = new MiddlewareApiFactory().WithSeedUser("demo", "demo1234");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "demo", password = "demo1234" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("demo", "")]
    [InlineData("demo", "   ")]
    [InlineData("", "demo1234")]
    public void StartupFailsFastWhenSeedingIsEnabledWithoutCredentials(string username, string password)
    {
        // The seeder would otherwise create an account with a blank password — reachable by anyone
        // who guesses the username. Not gated on the hosting environment: this factory hosts as
        // Staging and seeds deliberately, and an environment gate is defeated by setting
        // ASPNETCORE_ENVIRONMENT anyway.
        using var factory = new MiddlewareApiFactory().WithSeedUser(username, password);

        var failure = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(SeedUserOptions.SectionName, string.Join(" ", failure.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void StartupFailsFastWhenTheJwtSecretIsMissingOrTooShort(string secret)
    {
        using var factory = new MiddlewareApiFactory().WithJwtSecret(secret);

        // ValidateOnStart surfaces the failure while the host starts, not on the first login.
        var failure = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(nameof(JwtOptions.Secret), string.Join(" ", failure.Failures), StringComparison.Ordinal);
    }

    /// <summary>
    /// S7. Every one of these spellings is accepted by <c>WithOrigins</c> without complaint and then
    /// matches nothing — measured against a running host, a configured
    /// <c>"https://app.example.com/"</c> does not match an <c>Origin: https://app.example.com</c>
    /// request. No header is emitted and nothing is logged, so the only symptom is a browser client
    /// that is blocked for no visible reason. Start-up validation converts that into a message.
    /// </summary>
    [Theory]
    [InlineData("https://app.example.com/")]        // trailing slash: an Origin header never has one
    [InlineData("https://app.example.com/app")]     // a path is not part of an origin
    [InlineData("app.example.com")]                 // no scheme
    [InlineData("ftp://app.example.com")]           // not a browser origin
    [InlineData("*")]                               // AllowAnyOrigin by the back door
    [InlineData("  ")]
    public void StartupFailsFastOnAnOriginThatWouldSilentlyMatchNothing(string origin)
    {
        using var factory = new MiddlewareApiFactory().WithAllowedOrigins(origin);

        var failure = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(nameof(CorsPolicyOptions.AllowedOrigins), string.Join(" ", failure.Failures),
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupAcceptsOriginsWithAPortAndMixedCase()
    {
        // Host casing *is* normalized by the framework and matches either way, so it must not be
        // rejected; a non-default port is part of the origin and must be allowed through.
        using var factory = new MiddlewareApiFactory()
            .WithAllowedOrigins("http://localhost:5173", "https://APP.example.com");

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }
}
