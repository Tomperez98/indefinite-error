# indefinite-error for .NET

Inject **indefinite errors** ("it may or may not have happened") into the
requests your ASP.NET Core server handles. A marked call can end its request
right before or right after it runs, as if the request or its response were
lost. Then check that your retries can't apply a write twice.

```csharp
using IndefiniteError;

static readonly IndefiniteSite Commit = new("db.commit"); // a boundary write whose outcome can get lost

await Commit.RunAsync(() => tx.CommitAsync(ct));

builder.Services.AddIndefiniteErrors(); // tests only: requests carrying X-Indefinite-Seed get faults
app.UseIndefiniteErrors();
```

This is a port of the [Python package](../python). Every port is tested
against the same contract, [`spec/`](../spec), so a seed replays the same
faults in a .NET service, a Go one, a Rust one, a TypeScript one, and a
Python one.

## Install

Requires .NET 10. Two packages, with no dependencies beyond the framework:

| Package | For | Contains |
|---|---|---|
| `IndefiniteError` | any library | `IndefiniteSite`, `IndefiniteHttpHandler`, `IndefiniteErrorHeaders` |
| `IndefiniteError.AspNetCore` | the web app | `AddIndefiniteErrors`, `UseIndefiniteErrors`, `AddIndefiniteSite` |

A data-access library that marks its commits takes only `IndefiniteError`, not
ASP.NET Core. Until the packages are on NuGet, reference the projects from a
clone:

```sh
dotnet add reference path/to/indefinite-error/dotnet/src/IndefiniteError.AspNetCore
```

## One call, three outcomes

Outside a request that carries a seed, `site.Run(op)` and `site.RunAsync(op)`
just call `op`. Inside one, each call either:

| Outcome | `op` runs? | then |
|---|---|---|
| pass | yes | its real return value or exception |
| `before` | no | the request ends: it never happened |
| `after` | yes | the request ends: it happened, but nobody was told |

`before` and `after` are the two ways a write can be indefinite. Either the
state didn't change, or it changed and the response was lost. A retrying
client faces the same ambiguity it would in production.

A fault names the phase, the site, the call number, and the seed, so a CI log
is enough to replay it:

```text
indefinite-error: after app.get#4 (seed=13)
```

It writes that line to stderr and throws an `OperationCanceledException`,
because a faulted request is a cancelled one. `HttpContext.RequestAborted` is
cancelled too, so EF Core queries and `HttpClient` calls given that token stop.
Your `finally` blocks and `using` disposals run, and the server and its other
requests carry on. `UseIndefiniteErrors` then answers `500` with an
`X-Indefinite-Fault` header naming the fault.

.NET has no exception a `catch (Exception)` can't see, so a fault doesn't rely
on you rethrowing it. Once one fires, the request is over: every later marked
call in it throws without running, and the middleware answers `500` whatever
the handler returns. A retry loop, a catch-all, or an exception filter can
catch the fault, but can't undo it. Retries that spare cancellation, such as
Polly's default and `catch (Exception e) when (e is not OperationCanceledException)`,
never see it at all.

## Try it

The same `add(1)` under three seeds: one loses the write, one loses the
response, one is clean. This is [`examples/TryIt/TryIt.cs`](examples/TryIt/TryIt.cs),
one file: `dotnet run examples/TryIt/TryIt.cs`.

```csharp
#:sdk Microsoft.NET.Sdk.Web
#:project ../../src/IndefiniteError.AspNetCore/IndefiniteError.AspNetCore.csproj

using IndefiniteError;

var ledger = new List<int>();
var add = new IndefiniteSite("ledger.add"); // the boundary write that can lose its outcome

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddIndefiniteErrors();

var app = builder.Build();
app.UseIndefiniteErrors(); // tests only: requests carrying X-Indefinite-Seed get faults
app.MapPost("/deposits", () =>
{
    add.Run(() => ledger.Add(1));
    return "ok";
});
await app.StartAsync();

using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
foreach (var seed in new[] { "70", "74", "0" })
{
    ledger.Clear();
    using var request = new HttpRequestMessage(HttpMethod.Post, "/deposits");
    request.Headers.Add(IndefiniteErrorHeaders.Seed, seed);
    using var response = await client.SendAsync(request);
    Console.WriteLine($"seed={seed}: status={(int)response.StatusCode}, ledger=[{string.Join(", ", ledger)}]");
}

await app.StopAsync();
```

```text
indefinite-error: before ledger.add#0 (seed=70)
seed=70: status=500, ledger=[]
indefinite-error: after ledger.add#0 (seed=74)
seed=74: status=500, ledger=[1]
seed=0: status=200, ledger=[1]
```

`seed=70` faults *before*, so the write never happened. `seed=74` faults
*after*: the write committed, so a client that retries on the `500` applies it
a second time. That is the bug this package exists to find. `seed=0` runs
clean.

## Use it on your service

1. Declare an `IndefiniteSite` for each call whose outcome can get lost:
   database commits, calls to other services, messages you publish. Keep it in
   a `static readonly` field, and run the call through it:

   ```csharp
   public sealed class Ledger(AppDb db)
   {
       private static readonly IndefiniteSite Save = new("ledger.save");

       public Task AddAsync(Entry entry, CancellationToken ct) =>
           Save.RunAsync(() => db.SaveChangesAsync(ct));
   }
   ```

   For a call to another service, mark the `HttpClient` instead:

   ```csharp
   builder.Services.AddHttpClient<PaymentsClient>().AddIndefiniteSite("payments.charge");
   ```

   A `before` fault means the request is never sent; an `after` fault means it
   was sent and answered, and the response is disposed unread.

2. Register the middleware behind a flag that is off in production, early in
   the pipeline, before routing and endpoints:

   ```csharp
   var faults = builder.Configuration.GetValue<bool>("IndefiniteErrors");
   if (faults)
   {
       builder.Services.AddIndefiniteErrors();
   }

   var app = builder.Build();
   if (faults)
   {
       app.UseIndefiniteErrors();
   }
   ```

3. Send a different seed on every request, derived from one run seed. Treat a
   `500` as "may or may not have happened", and check your invariants
   afterwards. `WebApplicationFactory` works unchanged:
   `.WithWebHostBuilder(b => b.UseSetting("IndefiniteErrors", "true"))`. From
   .NET, [Accordant](https://microsoft.github.io/accordant/docs/how-to/indefinite-failures.html)
   drives the two branches for you.

## Example: a bank that must not double-count

[`examples/Bank`](examples/Bank) is a minimal-API + SQLite bank, and
[`examples/Bank.Tests`](examples/Bank.Tests) drives it through
`WebApplicationFactory` with a retrying client. Under injection an unkeyed
deposit double-counts, and the fault lines say why; an idempotency key fixes
it.

| | runs wrong (of 50) |
|---|---|
| unkeyed deposits | 39 — e.g. `balance 21, expected 20` |
| keyed deposits | 0 |

```sh
dotnet test examples/Bank.Tests
```

These are the same 39 runs every other port finds, because the seeds replay
the same faults in any language. To watch it by hand:

```sh
cd examples/Bank && dotnet run --IndefiniteErrors=true --urls http://localhost:8000
D='{"key": "deposit-1", "account": "alice", "amount": 1}'
curl -i localhost:8000/deposits -H 'content-type: application/json' -H 'x-indefinite-seed: 3' -d "$D"
curl -i localhost:8000/deposits -H 'content-type: application/json' -H 'x-indefinite-seed: 1' -d "$D"
curl -i localhost:8000/deposits -H 'content-type: application/json' -H 'x-indefinite-seed: 0' -d "$D"
curl localhost:8000/accounts/alice
```

Seed 3 faults *before* the write, seed 1 *after* it, seed 0 goes through, and
the key keeps the balance at 1.

## Details and caveats

- **Pass the operation, not its task.** `RunAsync` takes a `Func<Task>`, so
  `before` can skip it: `Commit.RunAsync(() => tx.CommitAsync())`, not
  `Commit.RunAsync(task)`. The operation must be finished when it returns:
  `Run` throws `InvalidOperationException` for a `Task`, `ValueTask`,
  `IQueryable`, or `IAsyncEnumerable` result, so `Run(async () => ...)` is
  caught on every call; an `async void` method passed as an `Action` is caught
  in seeded requests. Use `RunAsync`, or materialize the query
  (`ToListAsync()`) inside the operation. A lazy `IEnumerable` can't be
  detected: materialize it too.
- **Where a fault throws.** `Run` throws. `RunAsync` never throws
  synchronously for a fault: its task is cancelled.
- **Cancellation wins.** If the operation throws an `OperationCanceledException`,
  it propagates unchanged and an `after` fault stays silent. Any other
  exception is an outcome: `after` discards it.
- **The injection follows the `ExecutionContext`.** Everything the request
  starts sees it: `await`s, `Task.Run`, `Task.WhenAll`, timers, new threads.
  Work queued with `UnsafeQueueUserWorkItem`, or under
  `ExecutionContext.SuppressFlow()`, doesn't, and neither does a background
  service or a queue consumer: a call made there is never faulted, and nothing
  tells you so.
- **Too late to answer.** Once the response has started (a flushed body, a
  stream, SSE), the middleware can't answer `500`. It calls
  `HttpContext.Abort()`, and the client sees the connection drop
  mid-response. Until then, every write after the fault is swallowed, however
  it's made: `Response.Body`, `BodyWriter`, `SendFileAsync`, `IResult`.
- **A fault response is fresh.** It has an empty body, and none of the
  headers the lost request set, including any its `Response.OnStarting`
  callbacks add. Headers set by middleware outside `UseIndefiniteErrors`
  are kept. `AddIndefiniteErrors` sets Kestrel to send
  `X-Indefinite-Fault` as UTF-8, as the spec pins, so a non-ASCII site name
  survives; read it with `SocketsHttpHandler.ResponseHeaderEncodingSelector`.
- **Code after the fault still runs, but sees a failed request.** A handler
  that catches the fault, and the middleware between it and
  `UseIndefiniteErrors`, carry on as they would after a client disconnect.
  To them the response's status reads `500` and is fixed, so `UseOutputCache`
  and `UseResponseCaching` never store a lost response, wherever they sit.
  Any other side effect after the fault still happens unless it honors
  `RequestAborted`, as it would in production.
- **WebSockets.** A fault before `AcceptWebSocketAsync` answers the upgrade
  with `500`. A fault after it drops the socket.
- **Exception handlers stay out of it.** `UseExceptionHandler` and
  `UseDeveloperExceptionPage` see a cancelled `RequestAborted`, take the fault
  for a client that went away, and leave it to the middleware, wherever
  they sit in the pipeline.
- **Sites are names.** Each name draws its own faults, so calls to one site
  never shift another's; two sites with one name share one fault stream. A
  name must be non-empty and printable, with no whitespace, because it goes on
  the fault line: the constructor throws `ArgumentException` otherwise.
- **Seeds are `long`s.** A seed header is exactly one value of at most 19
  digits, optionally negative. A malformed, out-of-range, or repeated one gets
  a `400`, and the app never sees the request
  ([`spec/seed-header.tsv`](../spec/seed-header.tsv)).
- **Installing the middleware twice** throws on the first seeded request;
  `UseIndefiniteErrors` without `AddIndefiniteErrors` throws at startup.
- **After the request ends,** its injection closes. A task that outlives the
  request calls through with no faults.
- **Call numbers follow the order calls start.** Within one request, calls to
  one site are numbered in the order they're made. Concurrent calls are
  numbered in the order they start, not the order they finish.
- **Never install it in production.** Any caller could fault your server.

How seeds decide, and how this maps onto HTTP indefinite failures:
[How it works](../python/docs/how-it-works.md). The mechanics are the same;
where that page says `BaseException`, read "an `OperationCanceledException`
the injection remembers".

## Development

```sh
./ci          # the merge gate: format, build (warnings are errors), tests, coverage, pack
./ci fast     # just format and build
```

The tests check these packages against [`spec/`](../spec), the contract they
share with the other ports: the fault schedule, the fault lines, and which
seed headers are accepted. A change that shifts the schedule breaks every
saved seed, in every language. `ReadmeTests` checks that the "Try it" example
above is `examples/TryIt/TryIt.cs`, and that it prints what this README shows.
