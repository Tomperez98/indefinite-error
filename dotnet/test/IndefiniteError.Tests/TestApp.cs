using IndefiniteError.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace IndefiniteError.Tests;

/// <summary>A minimal-API app with the middleware installed, served by TestServer (or Kestrel), its fault lines captured.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    private TestApp(WebApplication app, HttpClient client, Captured faults)
    {
        App = app;
        Client = client;
        Faults = faults;
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public Captured Faults { get; }

    public static async Task<TestApp> StartAsync(
        Action<WebApplication> configure,
        Action<IServiceCollection>? services = null,
        bool kestrel = false,
        bool install = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (kestrel)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }
        else
        {
            builder.WebHost.UseTestServer();
        }

        var faults = new Captured();
        builder.Services.AddIndefiniteErrors();
        builder.Services.Replace(ServiceDescriptor.Singleton(new IndefiniteErrorsServices(faults.Log)));
        services?.Invoke(builder.Services);
        var app = builder.Build();
        if (install)
        {
            app.UseIndefiniteErrors();
        }

        configure(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = kestrel
            ? new HttpClient(new SocketsHttpHandler { ResponseHeaderEncodingSelector = (_, _) => System.Text.Encoding.UTF8 })
            {
                BaseAddress = new Uri(app.Urls.First()),
            }
            : app.GetTestClient();
        return new TestApp(app, client, faults);
    }

    /// <summary>One request, with <paramref name="seeds"/> as its seed header values (none: no header).</summary>
    public Task<HttpResponseMessage> SendAsync(string path, params string[] seeds)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (seeds.Length > 0)
        {
            request.Headers.TryAddWithoutValidation(IndefiniteErrorHeaders.Seed, seeds);
        }

        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}

internal static class ResponseExtensions
{
    public static string? Fault(this HttpResponseMessage response) =>
        response.Headers.TryGetValues(IndefiniteErrorHeaders.Fault, out var values) ? values.Single() : null;
}
