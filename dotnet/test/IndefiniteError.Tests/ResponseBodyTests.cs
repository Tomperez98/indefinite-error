using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace IndefiniteError.Tests;

/// <summary>Every way to write a response: forwarded until a fault, swallowed after it.</summary>
public sealed class ResponseBodyTests : IDisposable
{
    private static readonly IndefiniteSite Get = new("app.get");
    private readonly string _file = Path.GetTempFileName();

    public ResponseBodyTests() => File.WriteAllText(_file, "file");

    public void Dispose() => File.Delete(_file);

    public static TheoryData<string> Ways => new("stream-sync", "stream-array", "stream-flush-sync", "pipe", "sendfile", "complete", "start");

    private async Task Write(HttpContext context, string how)
    {
        var response = context.Response;
        var body = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        body.DisableBuffering();
        var bytes = "body"u8.ToArray();
        switch (how)
        {
            case "stream-sync":
                response.Body.Write(bytes, 0, bytes.Length);
                break;
            case "stream-array":
#pragma warning disable CA1835 // the point: the array overload
                await response.Body.WriteAsync(bytes, 0, bytes.Length);
#pragma warning restore CA1835
                break;
            case "stream-flush-sync":
                response.Body.Write(bytes);
                response.Body.Flush();
                break;
            case "pipe":
                bytes.CopyTo(response.BodyWriter.GetSpan(bytes.Length));
                response.BodyWriter.Advance(bytes.Length);
                await response.BodyWriter.FlushAsync();
                response.BodyWriter.Complete();
                break;
            case "sendfile":
                await response.SendFileAsync(_file);
                break;
            case "complete":
                await response.Body.WriteAsync(bytes);
                await response.CompleteAsync();
                break;
            default:
                await response.StartAsync();
                await response.BodyWriter.CompleteAsync();
                break;
        }

        Assert.True(response.Body.CanWrite);
        Assert.False(response.Body.CanRead || response.Body.CanSeek);
    }

    private async Task<TestApp> Start(bool fault, string how) => await TestApp.StartAsync(
        a => a.MapPost("/", async (HttpContext context) =>
        {
            if (fault)
            {
                try
                {
                    Get.Run(() => { });
                }
                catch (OperationCanceledException)
                {
                    // swallowed: the handler writes its response anyway
                }
            }

            await Write(context, how);
        }),
        services: s => s.Configure<TestServerOptions>(o => o.AllowSynchronousIO = true));

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task Without_a_fault_every_write_is_forwarded(string how)
    {
        await using var app = await Start(fault: false, how);
        using var response = await app.SendAsync("/", "0"); // seed 0: app.get is off
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = how switch { "sendfile" => "file", "start" => "", _ => "body" };
        Assert.Equal(expected, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task After_a_fault_every_write_is_swallowed(string how)
    {
        var seed = Helpers.Seeds.First(s => Schedule.Decide(s, Schedule.Rate(s), Schedule.ModeOf(s, Get.Name), Get.Name, 0) is not null);
        await using var app = await Start(fault: true, how);
        using var response = await app.SendAsync("/", seed.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotNull(response.Fault());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_stream_is_write_only()
    {
        using var scope = Helpers.Inject(0);
        var stream = new GuardedResponseBodyProbe(scope.Injection).Stream;
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    [Fact]
    public async Task The_writer_lends_scratch_memory_once_the_request_is_lost()
    {
        var pipe = new System.IO.Pipelines.Pipe();
        using var scope = Helpers.Inject(Helpers.Seeds.First(s => Schedule.Decide(s, Schedule.Rate(s), Schedule.ModeOf(s, Get.Name), Get.Name, 0) == Phase.Before));
        var inner = new StreamResponseBodyFeature(new MemoryStream());
        var writer = new IndefiniteError.AspNetCore.GuardedResponseBody(new PipeBody(pipe.Writer, inner), scope.Injection).Writer;
        writer.CancelPendingFlush(); // forwarded
        Assert.True((await writer.FlushAsync(TestContext.Current.CancellationToken)).IsCanceled);
        Assert.ThrowsAny<OperationCanceledException>(() => Get.Run(() => { })); // the request is lost
        var first = writer.GetMemory(10);
        var second = writer.GetMemory(10);
        Assert.Equal(first.Length, second.Length); // the scratch buffer, reused
        writer.Advance(10);
        await writer.FlushAsync(TestContext.Current.CancellationToken);
        await writer.CompleteAsync();
        Assert.False(pipe.Reader.TryRead(out _), "nothing reached the real pipe");
    }

    /// <summary>A body feature whose writer is <paramref name="writer"/>.</summary>
    private sealed class PipeBody(System.IO.Pipelines.PipeWriter writer, IHttpResponseBodyFeature rest) : IHttpResponseBodyFeature
    {
        public Stream Stream => rest.Stream;

        public System.IO.Pipelines.PipeWriter Writer => writer;

        public void DisableBuffering() => rest.DisableBuffering();

        public Task StartAsync(CancellationToken cancellationToken = default) => rest.StartAsync(cancellationToken);

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            rest.SendFileAsync(path, offset, count, cancellationToken);

        public Task CompleteAsync() => rest.CompleteAsync();
    }

    /// <summary>A guarded body over a plain stream, to poke at directly.</summary>
    private sealed class GuardedResponseBodyProbe(Injection injection)
    {
        public Stream Stream { get; } =
            new IndefiniteError.AspNetCore.GuardedResponseBody(new StreamResponseBodyFeature(new MemoryStream()), injection).Stream;
    }
}
