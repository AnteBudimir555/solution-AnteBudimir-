using Middleware.Core.Options;
using Middleware.Infrastructure.Security;

namespace Middleware.UnitTests.Security;

/// <summary>
/// Ported from <c>JwtServiceTest</c>: a valid round-trip returns the subject, and every failure mode
/// (expired, wrong secret, wrong issuer, tampered, malformed) yields no result rather than throwing —
/// the bearer middleware relies on that never-throw contract.
/// </summary>
public class JwtServiceTests
{
    private const string Secret = "unit-test-secret-key-that-is-quite-long-enough-for-hs256-0123456789";
    private const string Issuer = "abysalto-middleware";

    private static JwtService ServiceWith(string secret, string issuer, long expirationMinutes) =>
        new(new JwtOptions { Secret = secret, Issuer = issuer, ExpirationMinutes = expirationMinutes });

    private static JwtService Service() => ServiceWith(Secret, Issuer, 60);

    [Fact]
    public async Task IssuesAndValidatesTokenRoundTrip()
    {
        var service = Service();

        var token = service.IssueToken("demo");

        Assert.Equal("demo", await service.ExtractUsernameAsync(token));
    }

    [Fact]
    public void ReportsExpiryInSeconds()
    {
        Assert.Equal(3600, ServiceWith(Secret, Issuer, 60).ExpiresInSeconds);
    }

    [Fact]
    public async Task RejectsExpiredToken()
    {
        // Negative expiry issues an already-expired token.
        var expired = ServiceWith(Secret, Issuer, -1).IssueToken("demo");

        Assert.Null(await Service().ExtractUsernameAsync(expired));
    }

    [Fact]
    public async Task RejectsTokenSignedWithDifferentSecret()
    {
        var token = ServiceWith("another-secret-key-of-sufficient-length-for-hs256-abcdefghij", Issuer, 60)
            .IssueToken("demo");

        Assert.Null(await Service().ExtractUsernameAsync(token));
    }

    [Fact]
    public async Task RejectsTokenWithUnexpectedIssuer()
    {
        var token = ServiceWith(Secret, "someone-else", 60).IssueToken("demo");

        Assert.Null(await Service().ExtractUsernameAsync(token));
    }

    [Fact]
    public async Task RejectsTamperedToken()
    {
        var token = Service().IssueToken("demo");
        // Flip the final character of the signature.
        var last = token[^1];
        var tampered = token[..^1] + (last == 'a' ? 'b' : 'a');

        Assert.Null(await Service().ExtractUsernameAsync(tampered));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    [InlineData("header.payload")]
    public async Task RejectsMalformedTokenWithoutThrowing(string? malformed)
    {
        Assert.Null(await Service().ExtractUsernameAsync(malformed));
    }

    [Fact]
    public void IssuedTokenCarriesTheJavaClaimSet()
    {
        var service = Service();

        var token = service.IssueToken("demo");
        var payload = DecodePayload(token);

        // sub / iss / iat / exp and nothing else — jjwt emits no nbf, and Microsoft's default would.
        Assert.Equal(["exp", "iat", "iss", "sub"], payload.Keys.Order());
        Assert.Equal("demo", payload["sub"].GetString());
        Assert.Equal(Issuer, payload["iss"].GetString());
        Assert.Equal(3600, payload["exp"].GetInt64() - payload["iat"].GetInt64());
    }

    private static Dictionary<string, System.Text.Json.JsonElement> DecodePayload(string token)
    {
        var segment = token.Split('.')[1];
        var padded = segment.Replace('-', '+').Replace('_', '/').PadRight((segment.Length + 3) / 4 * 4, '=');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;
    }
}
