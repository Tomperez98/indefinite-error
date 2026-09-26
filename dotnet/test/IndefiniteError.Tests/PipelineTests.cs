using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace IndefiniteError.Tests;

/// <summary>The middleware among other ASP.NET Core features: starting callbacks, caches, WebSockets.</summary>
public sealed class PipelineTests
{
    private static readonly IndefiniteSite Get = new("app.get");

    private static long SeedFaulting(Phase phase, int n = 0) =>
        Helpers.Seeds.First(s => Schedule.Decide(s, Schedule.Rate(s), Schedule.ModeOf(s, Get.Name), Get.Name, n) == phase);

    private static string Seed(Phase phase) => SeedFaulting(phase).ToString(CultureInfo.InvariantCulture);

    /// <summary>Calls the site, swallows the fault, and answers as if nothing happened.</summary>
    private static string Swallowing()
    {
        try
        {
            Get.Run(() => { });
        }
        catch (OperationCanceledException)
        {
        }

        return "a 200 nobody may see";
    }

    // --- Starting callbacks ---

    [Fact]
    public async Task Headers_from_the_lost_requests_starting_callbacks_do_not_survive()
    {
        await using var app = await TestApp.StartAsync(
            a =>
            {
                a.Use((context, next) =>
                {
                    context.Response.OnStarting(() =>
                    {
                        context.Response.Headers["X-Outer-Late"] = "kept";
                        return Task.CompletedTask;
                    });
                    return next(context);
                });
                a.UseIndefiniteErrors();
                a.MapPost("/", (HttpContext context) =>
                {
                    context.Response.OnStarting(() =>
                    {
                        context.Response.Headers["Set-Cookie"] = "session=lost";
                        context.Response.StatusCode = StatusCodes.Status201Created;
                        return Task.CompletedTask;
                    });
                    return Swallowing();
                });
            },
            install: false);
        using var response = await app.SendAsync("/", Seed(Phase.Before));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("kept", response.Headers.GetValues("X-Outer-Late").Single());
        Assert.NotNull(response.Fault());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Starting_callbacks_are_untouched_without_a_fault()
    {
        await using var app = await TestApp.StartAsync(a => a.MapPost("/", (HttpContext context) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["Set-Cookie"] = "session=kept";
                return Task.CompletedTask;
            });
            Get.Run(() => { });
            return "ok";
        }));
        using var response = await app.SendAsync("/", "0"); // seed 0: app.get is off
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("session=kept", response.Headers.GetValues("Set-Cookie").Single());
    }

    // --- Caches: a lost response must never be served to anyone ---

    [Theory]
    [InlineData("output", true)]
    [InlineData("output", false)]
    [InlineData("response", true)]
    [InlineData("response", false)]
    public async Task A_cache_never_keeps_a_lost_response(string cache, bool cacheInside)
    {
        void Cache(WebApplication a)
        {
            if (cache == "output")
            {
                a.UseOutputCache();
            }
            else
            {
                a.UseResponseCaching();
            }
        }

        var served = 0;
        await using var app = await TestApp.StartAsync(
            a =>
            {
                if (!cacheInside)
                {
                    Cache(a);
                }

                a.UseIndefiniteErrors();
                if (cacheInside)
                {
                    Cache(a);
                }

                var endpoint = a.MapGet("/", (HttpContext context) =>
                {
                    context.Response.Headers.CacheControl = "public, max-age=60";
                    return $"{Swallowing()} #{Interlocked.Increment(ref served)}";
                });
                if (cache == "output")
                {
                    endpoint.CacheOutput();
                }
            },
            services: s =>
            {
                s.AddOutputCache();
                s.AddResponseCaching();
            },
            install: false);
        var ct = TestContext.Current.CancellationToken;

        using (var lost = new HttpRequestMessage(HttpMethod.Get, "/"))
        {
            lost.Headers.Add(IndefiniteErrorHeaders.Seed, Seed(Phase.Before));
            using var response = await app.Client.SendAsync(lost, ct);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        // An unseeded request after it must reach the app, not a cached copy of the lost response.
        Assert.Equal("a 200 nobody may see #2", await app.Client.GetStringAsync(new Uri("/", UriKind.Relative), ct));

        // And the cache is live: it keeps that real response.
        Assert.Equal("a 200 nobody may see #2", await app.Client.GetStringAsync(new Uri("/", UriKind.Relative), ct));
    }

    // --- WebSockets ---

    private static async Task<TestApp> WebSocketApp() => await TestApp.StartAsync(
        a =>
        {
            a.UseWebSockets();
            a.Map("/ws", async (HttpContext context) =>
            {
                Get.Run(() => { }); // call 0: before the upgrade
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[16];
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
                        return;
                    }

                    Get.Run(() => { }); // call 1..: after the upgrade
                    await socket.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Text, true, context.RequestAborted);
                }
            });
        },
        install: true);

    private static WebSocketClient Client(TestApp app, long seed)
    {
        var client = app.App.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers[IndefiniteErrorHeaders.Seed] = seed.ToString(CultureInfo.InvariantCulture);
        return client;
    }

    [Fact]
    public async Task A_fault_before_the_upgrade_answers_500()
    {
        await using var app = await WebSocketApp();
        var seed = SeedFaulting(Phase.Before);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Client(app, seed).ConnectAsync(new Uri("ws://localhost/ws"), TestContext.Current.CancellationToken));
        Assert.Contains("500", error.Message, StringComparison.Ordinal);
        Assert.Equal([$"indefinite-error: before app.get#0 (seed={seed})\n"], app.Faults.Lines);
    }

    [Fact]
    public async Task A_fault_after_the_upgrade_drops_the_socket()
    {
        await using var app = await WebSocketApp();
        var ct = TestContext.Current.CancellationToken;
        var seed = Helpers.Seeds.First(s =>
            Schedule.Decide(s, Schedule.Rate(s), Schedule.ModeOf(s, Get.Name), Get.Name, 0) is null
            && Schedule.Decide(s, Schedule.Rate(s), Schedule.ModeOf(s, Get.Name), Get.Name, 1) is not null);
        using var socket = await Client(app, seed).ConnectAsync(new Uri("ws://localhost/ws"), ct);
        await socket.SendAsync("ping"u8.ToArray(), WebSocketMessageType.Text, true, ct);
        var error = await Record.ExceptionAsync(async () =>
        {
            var received = await socket.ReceiveAsync(new byte[16], ct);
            Assert.Fail($"the socket answered {received.MessageType} after a fault");
        });
        Assert.IsAssignableFrom<Exception>(error);
        Assert.IsNotType<Xunit.Sdk.FailException>(error);
        Assert.Single(app.Faults.Lines);
    }

    [Fact]
    public async Task A_seeded_socket_without_faults_works()
    {
        await using var app = await WebSocketApp();
        var ct = TestContext.Current.CancellationToken;
        using var socket = await Client(app, 0).ConnectAsync(new Uri("ws://localhost/ws"), ct); // seed 0: app.get is off
        await socket.SendAsync("ping"u8.ToArray(), WebSocketMessageType.Text, true, ct);
        var buffer = new byte[16];
        var received = await socket.ReceiveAsync(buffer, ct);
        Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        Assert.Empty(app.Faults.Lines);
    }
}

/// <summary>The guarded response feature, poked at directly.</summary>
public sealed class GuardedResponseTests
{
    [Fact]
    public void It_forwards_until_the_request_is_lost_then_reads_500()
    {
        var get = new IndefiniteSite("app.get");
        var seed = Helpers.Seeds.First(s => Schedule.Decide(s, Schedule.Rate(s), Schedule.ModeOf(s, get.Name), get.Name, 0) == Phase.Before);
        using var scope = Helpers.Inject(seed);
        var inner = new Microsoft.AspNetCore.Http.Features.HttpResponseFeature();
        var guarded = new IndefiniteError.AspNetCore.GuardedResponse(inner, scope.Injection);

        guarded.StatusCode = 201;
        guarded.ReasonPhrase = "Made";
        var headers = new HeaderDictionary { ["X-A"] = "1" };
        guarded.Headers = headers;
#pragma warning disable CS0618 // the obsolete Body is still part of the feature
        var body = new MemoryStream();
        guarded.Body = body;
        Assert.Same(body, guarded.Body);
#pragma warning restore CS0618
        guarded.OnStarting(_ => Task.CompletedTask, new object());
        guarded.OnCompleted(_ => Task.CompletedTask, new object());
        Assert.Equal(201, guarded.StatusCode);
        Assert.Equal("Made", guarded.ReasonPhrase);
        Assert.Same(headers, guarded.Headers);
        Assert.False(guarded.HasStarted);

        Assert.ThrowsAny<OperationCanceledException>(() => get.Run(() => { }));
        guarded.StatusCode = 200; // after the fault: ignored
        Assert.Equal(500, guarded.StatusCode);
        Assert.Equal(201, inner.StatusCode);
    }
}
