using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IndefiniteError.Tests;

/// <summary>An outgoing HttpClient call as a site: before, it's never sent; after, its response is lost.</summary>
public sealed class HttpHandlerTests
{
    private const string Payments = "payments.charge";

    /// <summary>The service on the other end: counts what it receives, and tracks the responses it sent.</summary>
    private sealed class Upstream : HttpMessageHandler
    {
        public int Received { get; private set; }

        public List<TrackedContent> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Received++;
            var content = new TrackedContent();
            Sent.Add(content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class TrackedContent() : ByteArrayContent("charged"u8.ToArray())
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static long SeedFaulting(Phase phase) =>
        Helpers.Seeds.First(s => Schedule.Decide(s, Schedule.Rate(s), Schedule.ModeOf(s, Payments), Payments, 0) == phase);

    [Theory]
    [InlineData("pass")]
    [InlineData("before")]
    [InlineData("after")]
    public async Task An_outgoing_call_is_lost_before_or_after_it_is_sent(string outcome)
    {
        Phase? phase = outcome switch { "before" => Phase.Before, "after" => Phase.After, _ => null };
        var upstream = new Upstream();
        using var client = new HttpClient(new IndefiniteHttpHandler(new IndefiniteSite(Payments), upstream));
        var seed = phase is { } p ? SeedFaulting(p) : Helpers.Seeds.First(s => Schedule.ModeOf(s, Payments) == Mode.Off);
        using var _ = Helpers.Inject(seed);

        var call = client.GetAsync(new Uri("http://payments/charge"), TestContext.Current.CancellationToken);
        if (phase is null)
        {
            using var response = await call;
            Assert.Equal(1, upstream.Received);
            Assert.False(upstream.Sent[0].Disposed);
            return;
        }

        // HttpClient reports a cancellation it didn't ask for as a timeout; either way, an OperationCanceledException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.NotNull(Injection.Current!.Aborted);
        Assert.Equal(phase == Phase.After ? 1 : 0, upstream.Received);
        Assert.All(upstream.Sent, content => Assert.True(content.Disposed, "the lost response is disposed"));
    }

    [Fact]
    public void The_synchronous_send_is_a_site_call_too()
    {
        var upstream = new Upstream();
        using var client = new HttpClient(new IndefiniteHttpHandler(new IndefiniteSite(Payments), upstream));
        using var _ = Helpers.Inject(SeedFaulting(Phase.After));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("http://payments/charge"));
        Assert.ThrowsAny<OperationCanceledException>(() => client.Send(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, upstream.Received);
        Assert.True(upstream.Sent[0].Disposed);
    }

    [Fact]
    public void The_handler_needs_a_site()
    {
        Assert.Throws<ArgumentNullException>(() => new IndefiniteHttpHandler(null!));
        Assert.Throws<ArgumentNullException>(() => new IndefiniteHttpHandler(null!, new Upstream()));
        Assert.Equal("x.y", new IndefiniteHttpHandler(new IndefiniteSite("x.y")).Site.Name);
    }

    [Fact]
    public void AddIndefiniteSite_checks_the_name_when_registered()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddHttpClient("payments").AddIndefiniteSite("not a site"));
    }

    [Theory]
    [InlineData("before", 0)]
    [InlineData("after", 1)]
    public async Task A_factory_client_faults_the_request_that_called_it(string outcome, int received)
    {
        var phase = outcome == "before" ? Phase.Before : Phase.After;
        // A seeded request calls another service through IHttpClientFactory: the whole request is lost.
        var upstream = new Upstream();
        await using var app = await TestApp.StartAsync(
            a => a.MapPost("/checkout", async (IHttpClientFactory factory, HttpContext context) =>
            {
                using var client = factory.CreateClient("payments");
                using var charged = await client.PostAsync(new Uri("http://payments/charge"), null, context.RequestAborted);
                return "paid";
            }),
            services: s => s.AddHttpClient("payments").AddIndefiniteSite(Payments).ConfigurePrimaryHttpMessageHandler(() => upstream));
        using var response = await app.SendAsync("/checkout", SeedFaulting(phase).ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.StartsWith($"{phase.Wire()} {Payments}#0 ", response.Fault(), StringComparison.Ordinal);
        Assert.Equal(received, upstream.Received);
    }
}
