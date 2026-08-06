using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Core.Abstractions;
using Middleware.Core.Options;

namespace Middleware.Infrastructure.Upstream;

/// <summary>
/// DI wiring for the DummyJSON upstream adapter: binds <see cref="UpstreamOptions"/> and registers
/// <see cref="DummyJsonProductSource"/> as the <see cref="IProductSource"/>, backed by a typed
/// <see cref="HttpClient"/> whose base address, response timeout and connect timeout come from options.
/// </summary>
public static class UpstreamServiceCollectionExtensions
{
    public static IServiceCollection AddUpstreamSource(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<UpstreamOptions>(configuration.GetSection(UpstreamOptions.SectionName));

        var options = configuration.GetSection(UpstreamOptions.SectionName).Get<UpstreamOptions>() ?? new UpstreamOptions();

        // BaseAddress must end with '/' so the source's relative URIs (e.g. "products/1") resolve under it.
        var baseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

        services.AddHttpClient<IProductSource, DummyJsonProductSource>(client =>
            {
                client.BaseAddress = baseAddress;
                // Overall per-request timeout ~ the upstream response timeout.
                client.Timeout = TimeSpan.FromMilliseconds(options.ResponseTimeoutMs);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Separate, shorter budget for establishing the TCP/TLS connection.
                ConnectTimeout = TimeSpan.FromMilliseconds(options.ConnectTimeoutMs),
                AutomaticDecompression = DecompressionMethods.All
            });

        return services;
    }
}
