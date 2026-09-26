using System.Globalization;
using System.Net;
using System.Text.Json;
using IndefiniteError.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace IndefiniteError.Tests;

/// <summary>The middleware: which requests it seeds, and how it answers for a fault.</summary>
public sealed class MiddlewareTests
{
    private static readonly IndefiniteSite Get = new("app.get");

    /// <summary>
    /// <c>POST /calls?site=..&amp;count=..</c> calls the site <c>count</c> times,
    /// then answers 200 with the request's seed, or <c>none</c>.
    /// </summary>
    private static void Calls(WebApplication app) =>
        app.MapPost("/calls", (string site, int count) =>
        {
            var marked = new IndefiniteSite(site);
            for (var i = 0; i < count; i++)
            {
                marked.Run(() => { });
            }

            return Injection.Current?.Seed.ToString(CultureInfo.InvariantCulture) ?? "none";
        });

    private static string CallsPath(string site, int count) => $"/calls?site={Uri.EscapeDataString(site)}&count={count}";

    /// <summary>The first seed, from 0, whose <paramref name="n"/>-th call to <paramref name="site"/> faults in <paramref name="phase"/>.</summary>
    private static long SeedFaulting(string site, Phase phase, int n = 0) =>
        Helpers.Seeds.First(s => Schedule.Decide(s, Schedule.Rate(s), Schedule.ModeOf(s, site), site, n) == phase);

    // --- Which requests get an injection ---

    [Fact]
    public async Task A_request_without_the_header_runs_untouched()
    {
        await using var app = await TestApp.StartAsync(Calls);
        using var response = await app.SendAsync(CallsPath("app.get", 200));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("none", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(app.Faults.Lines);
    }

    public static TheoryData<string[], string> SeedHeaderRows() =>
        new(Spec.Rows("seed-header.tsv").Select(row => (JsonSerializer.Deserialize<string[]>(row[0])!, row[1])));

    [Theory]
    [MemberData(nameof(SeedHeaderRows))]
    public async Task The_seed_header_is_read_as_the_spec_pins(string[] values, string outcome)
    {
        // The values as the server holds them: on the wire, HTTP trims whitespace and
        // HttpClient refuses non-ASCII, so the context is built directly.
        var reached = false;
        await using var app = await TestApp.StartAsync(a => a.MapPost("/", () =>
        {
            reached = true;
            return Injection.Current!.Seed.ToString(CultureInfo.InvariantCulture);
        }));
        var context = await app.App.GetTestServer().SendAsync(
            c =>
            {
                c.Request.Method = "POST";
                c.Request.Path = "/";
                c.Request.Headers[IndefiniteErrorHeaders.Seed] = new StringValues(values);
            },
            TestContext.Current.CancellationToken);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        if (outcome == "400")
        {
            Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
            Assert.Equal("X-Indefinite-Seed must be a decimal int64", body);
            Assert.False(reached, "the app must never see the request");
        }
        else
        {
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal(outcome, body);
        }
    }

    [Fact]
    public void Seed_parsing_agrees_with_the_spec_without_a_server()
    {
        foreach (var row in Spec.Rows("seed-header.tsv"))
        {
            var ok = SeedHeader.TryParse(new StringValues(JsonSerializer.Deserialize<string[]>(row[0])), out var seed);
            Assert.Equal(row[1] != "400", ok);
            if (ok)
            {
                Assert.Equal(row[1], seed.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    // --- The fault header: the spec's seeds and sites, over the wire ---

    public static TheoryData<long, string, int> FaultRows() =>
        new(Spec.Rows("faults.tsv").Select(row =>
            (long.Parse(row[0], CultureInfo.InvariantCulture), Spec.Site(row[1]), int.Parse(row[2], CultureInfo.InvariantCulture))));

    /// <summary>The first fault in a request seeded <paramref name="seed"/> that calls <paramref name="site"/> <paramref name="count"/> times.</summary>
    private static string FirstFault(long seed, string site, int count)
    {
        var rate = Schedule.Rate(seed);
        var mode = Schedule.ModeOf(seed, site);
        var n = Enumerable.Range(0, count).First(i => Schedule.Decide(seed, rate, mode, site, i) is not null);
        return new Fault(seed, site, n, Schedule.Decide(seed, rate, mode, site, n)!.Value).ToString();
    }

    [Theory]
    [MemberData(nameof(FaultRows))]
    public async Task A_fault_answers_500_naming_it_and_writes_its_line(long seed, string site, int n)
    {
        var payload = FirstFault(seed, site, n + 1);
        await using var app = await TestApp.StartAsync(Calls);
        using var response = await app.SendAsync(CallsPath(site, n + 1), seed.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(payload, response.Fault());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal([$"indefinite-error: {payload}\n"], app.Faults.Lines);
    }

    [Theory]
    [MemberData(nameof(FaultRows))]
    public async Task Kestrel_sends_the_fault_header_as_utf8(long seed, string site, int n)
    {
        await using var app = await TestApp.StartAsync(Calls, kestrel: true);
        using var response = await app.SendAsync(CallsPath(site, n + 1), seed.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(FirstFault(seed, site, n + 1), response.Fault());
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    // --- The fault response is fresh ---

    [Fact]
    public async Task A_fault_response_keeps_only_what_outer_middleware_set()
    {
        var seed = SeedFaulting(Get.Name, Phase.After);
        await using var outer = await TestApp.StartAsync(
            a =>
            {
                a.Use((context, next) =>
                {
                    context.Response.Headers["X-Outer"] = "kept";
                    return next(context);
                });
                a.UseIndefiniteErrors();
                a.MapPost("/", (HttpContext context) =>
                {
                    context.Response.Headers["X-Inner"] = "lost";
                    context.Response.StatusCode = StatusCodes.Status201Created;
                    Get.Run(() => { });
                    return "unreachable";
                });
            },
            install: false);
        using var response = await outer.SendAsync("/", seed.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("kept", response.Headers.GetValues("X-Outer").Single());
        Assert.False(response.Headers.Contains("X-Inner"));
        Assert.Equal($"after app.get#0 (seed={seed})", response.Fault());
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("pipe")]
    [InlineData("results")]
    public async Task A_swallowed_fault_still_ends_the_request(string how)
    {
        var seed = SeedFaulting(Get.Name, Phase.Before);
        var aborted = false;
        await using var app = await TestApp.StartAsync(a => a.MapPost("/", async (HttpContext context) =>
        {
            try
            {
                Get.Run(() => { });
            }
            catch (Exception)
            {
                // A catch-all that pretends nothing happened.
            }

            aborted = context.RequestAborted.IsCancellationRequested;
            var bytes = "a 200 nobody may see"u8.ToArray();
            switch (how)
            {
                case "stream":
                    await context.Response.Body.WriteAsync(bytes);
                    await context.Response.Body.FlushAsync();
                    break;
                case "pipe":
                    await context.Response.BodyWriter.WriteAsync(bytes);
                    await context.Response.StartAsync();
                    break;
                default:
                    await Results.Ok(new { status = "ok" }).ExecuteAsync(context);
                    break;
            }
        }));
        using var response = await app.SendAsync("/", seed.ToString(CultureInfo.InvariantCulture));
        Assert.True(aborted, "RequestAborted is cancelled once a fault ends the request");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal($"before app.get#0 (seed={seed})", response.Fault());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_fault_after_the_response_started_drops_the_connection()
    {
        var seed = SeedFaulting(Get.Name, Phase.After);
        await using var app = await TestApp.StartAsync(a => a.MapPost("/", async (HttpContext context) =>
        {
            await context.Response.WriteAsync("partial");
            await context.Response.Body.FlushAsync();
            Get.Run(() => { });
        }), kestrel: true);
        var error = await Record.ExceptionAsync(async () =>
        {
            using var response = await app.SendAsync("/", seed.ToString(CultureInfo.InvariantCulture));
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        });
        Assert.IsType<HttpRequestException>(error, exactMatch: false);
        Assert.Equal([$"indefinite-error: after app.get#0 (seed={seed})\n"], app.Faults.Lines);
    }

    [Fact]
    public async Task A_seeded_request_without_faults_answers_normally()
    {
        await using var app = await TestApp.StartAsync(a => a.MapPost("/", async (HttpContext context) =>
        {
            context.Response.Headers["X-App"] = "yes";
            await context.Response.Body.WriteAsync("stream,"u8.ToArray());
            await context.Response.BodyWriter.WriteAsync("pipe"u8.ToArray());
        }));
        using var response = await app.SendAsync("/", "0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("yes", response.Headers.GetValues("X-App").Single());
        Assert.Equal("stream,pipe", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // --- Exceptions ---

    [Fact]
    public async Task An_exception_that_is_not_a_fault_propagates()
    {
        await using var app = await TestApp.StartAsync(a => a.MapPost("/", string () => throw new DefiniteException()));
        await Assert.ThrowsAsync<DefiniteException>(() => app.SendAsync("/", "0"));
    }

    [Fact]
    public async Task Any_exception_after_a_fault_is_part_of_the_lost_request()
    {
        var seed = SeedFaulting(Get.Name, Phase.Before);
        await using var app = await TestApp.StartAsync(a => a.MapPost("/", string () =>
        {
            try
            {
                Get.Run(() => { });
            }
            catch (OperationCanceledException)
            {
                throw new AggregateException(new DefiniteException());
            }

            return "unreachable";
        }));
        using var response = await app.SendAsync("/", seed.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotNull(response.Fault());
    }

    [Theory]
    [InlineData("handler", true)]
    [InlineData("handler", false)]
    [InlineData("developer", true)]
    [InlineData("developer", false)]
    public async Task The_exception_handler_leaves_the_fault_to_the_middleware(string which, bool handlerInside)
    {
        // A cancelled RequestAborted tells an exception handler the client is gone: it doesn't answer.
        void Handler(IApplicationBuilder a)
        {
            if (which == "handler")
            {
                a.UseExceptionHandler(e => e.Run(c => c.Response.WriteAsync("handled")));
            }
            else
            {
                a.UseDeveloperExceptionPage();
            }
        }

        var seed = SeedFaulting(Get.Name, Phase.After);
        await using var app = await TestApp.StartAsync(
            a =>
            {
                if (!handlerInside)
                {
                    Handler(a);
                }

                a.UseIndefiniteErrors();
                if (handlerInside)
                {
                    Handler(a);
                }

                a.MapPost("/", () => Get.Run(() => "unreachable"));
            },
            services: s => s.AddProblemDetails(),
            install: false);
        using var response = await app.SendAsync("/", seed.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal($"after app.get#0 (seed={seed})", response.Fault());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    // --- Installation and lifecycle ---

    [Fact]
    public async Task Installing_without_the_services_fails_at_startup()
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var error = Assert.Throws<InvalidOperationException>(() => app.UseIndefiniteErrors());
        Assert.Contains("AddIndefiniteErrors", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddIndefiniteErrors_keeps_any_other_header_encoding()
    {
        var services = new ServiceCollection();
        services.Configure<KestrelServerOptions>(o => o.ResponseHeaderEncodingSelector = name => name == "X-Latin" ? System.Text.Encoding.Latin1 : null);
        services.AddIndefiniteErrors();
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.Equal(System.Text.Encoding.UTF8, options.ResponseHeaderEncodingSelector("x-indefinite-fault"));
        Assert.Equal(System.Text.Encoding.Latin1, options.ResponseHeaderEncodingSelector("X-Latin"));
        Assert.Null(options.ResponseHeaderEncodingSelector("X-Other"));

        var alone = new ServiceCollection().AddIndefiniteErrors().BuildServiceProvider().GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.Null(alone.ResponseHeaderEncodingSelector("X-Other"));
    }

    [Fact]
    public async Task Installing_twice_fails_on_the_first_seeded_request()
    {
        await using var app = await TestApp.StartAsync(a =>
        {
            a.UseIndefiniteErrors();
            Calls(a);
        });
        using (var plain = await app.SendAsync(CallsPath("app.get", 1)))
        {
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.SendAsync(CallsPath("app.get", 1), "7"));
        Assert.Equal("indefinite: a request seeded 7 inside one seeded 7: is UseIndefiniteErrors installed twice?", error.Message);
    }

    [Fact]
    public async Task A_task_that_outlives_its_request_is_never_faulted()
    {
        var seed = SeedFaulting(Get.Name, Phase.Before, n: 1);
        var release = new TaskCompletionSource();
        Task<int>? late = null;
        await using var app = await TestApp.StartAsync(a => a.MapPost("/", () =>
        {
            Get.Run(() => { }); // call 0 passes
            late = Task.Run(async () =>
            {
                await release.Task;
                return Enumerable.Range(0, 200).Sum(_ => Get.Run(() => 1));
            });
            return "ok";
        }));
        using var response = await app.SendAsync("/", seed.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        release.SetResult();
        Assert.Equal(200, await late!);
        Assert.Empty(app.Faults.Lines);
    }

    [Fact]
    public async Task Concurrent_requests_with_one_seed_take_one_path()
    {
        await using var app = await TestApp.StartAsync(Calls);
        async Task<string> Outcome()
        {
            using var response = await app.SendAsync(CallsPath("app.get", 30), "3");
            return $"{(int)response.StatusCode} {response.Fault()}";
        }

        var first = await Outcome();
        var all = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(Outcome)));
        Assert.All(all, outcome => Assert.Equal(first, outcome));
        Assert.StartsWith("500 ", first, StringComparison.Ordinal);
    }
}
