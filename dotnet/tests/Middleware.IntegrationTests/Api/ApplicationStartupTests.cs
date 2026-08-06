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
    [InlineData("")]
    [InlineData("too-short")]
    public void StartupFailsFastWhenTheJwtSecretIsMissingOrTooShort(string secret)
    {
        using var factory = new MiddlewareApiFactory().WithJwtSecret(secret);

        // ValidateOnStart surfaces the failure while the host starts, not on the first login.
        var failure = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(nameof(JwtOptions.Secret), string.Join(" ", failure.Failures), StringComparison.Ordinal);
    }
}
