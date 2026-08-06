using System.Net;

namespace Middleware.ShadowHarness;

/// <summary>
/// A counting reverse proxy in front of the real DummyJSON. Each service is pointed at its own
/// instance, which is what makes "how many upstream calls did this request storm cost?" a measured
/// number rather than an inference from latency — and therefore what lets the load run prove the
/// caches deduplicate identically.
///
/// <para>Both instances forward to the same origin and add the same hop, so the latency they
/// contribute is common to both sides of the comparison.</para>
/// </summary>
internal sealed class UpstreamCounter : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _origin;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private int _calls;

    public UpstreamCounter(int port, string originBaseUrl)
    {
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _origin = new HttpClient { BaseAddress = new Uri(originBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    public int Port { get; }

    /// <summary>Upstream requests forwarded since the last <see cref="Reset"/>.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public void Reset() => Interlocked.Exchange(ref _calls, 0);

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return; // listener stopped
            }

            // Deliberately not awaited: concurrent forwarding is the point — a proxy that serialized
            // requests would hide exactly the stampede behaviour under test.
            _ = ForwardAsync(context);
        }
    }

    private async Task ForwardAsync(HttpListenerContext context)
    {
        Interlocked.Increment(ref _calls);
        try
        {
            var target = context.Request.Url?.PathAndQuery ?? "/";
            var response = await _origin.GetAsync(target, _stopping.Token);
            var body = await response.Content.ReadAsByteArrayAsync(_stopping.Token);

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await context.Response.OutputStream.WriteAsync(body, _stopping.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or HttpListenerException)
        {
            TrySetBadGateway(context);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (HttpListenerException)
            {
                // The client gave up first; nothing to report.
            }
        }
    }

    private static void TrySetBadGateway(HttpListenerContext context)
    {
        try
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
        {
            // Response already committed or the connection is gone.
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        _loop?.Wait(TimeSpan.FromSeconds(2));
        _origin.Dispose();
        _stopping.Dispose();
    }
}
