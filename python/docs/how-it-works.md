# How indefinite-error works

The root [README](../README.md) covers the pitch and the quick start. This page
is the mechanics: what a fault does, how seeds decide, how it maps onto HTTP
indefinite failures, and the full list of caveats.

## A fault ends the request

Every fault first writes one line to stderr:

```text
indefinite-error: after app.get#4 (seed=13)
```

It names the phase, the site, the call number, and the seed, so a CI log is
enough to replay it.

Then a private exception unwinds the request to the middleware, which answers
for it. Cleanup runs -- `finally` blocks, `with` exits, lock releases -- as it
would when a request is cancelled. The server and its other requests carry on,
and its memory survives.

The exception is a `BaseException`, so `except Exception` handlers and retry
loops don't see it, and it isn't exported, so nothing else can name it to catch
it. Only code that catches `BaseException` without re-raising would swallow it --
as it would swallow a cancellation. A teardown the marked call raises --
`KeyboardInterrupt`, `SystemExit`, `asyncio.CancelledError` -- outranks the
fault and propagates unchanged.

The same design carries to runtimes that can abort one request:
`panic(http.ErrAbortHandler)` in Go, a panic caught at the edge by axum's
`CatchPanicLayer` in Rust.

## The seed is the only knob

Each seed picks a fault rate and, per function, which phases may fault (`off`,
`before`, `after`, `both`), so sweeping seeds covers gentle and brutal runs
alike (swarm testing). Every decision is a hash of `(seed, function, call
number)`, counted within the request: the same seed replays the same faults, in
any process, and calls to one function never shift another's faults. The
[Go port](../../go) makes the same decisions: both are tested against
[`spec/`](../../spec), which defines the hash exactly.

## Over HTTP, with Accordant

[Accordant](https://microsoft.github.io/accordant/docs/how-to/indefinite-failures.html)
models an indefinite failure as two branches: the request was lost (state
unchanged) or the response was lost (state changed). `before` and `after`
cause exactly those two, inside your server:

| Server | Client sees | Accordant branch |
|---|---|---|
| no fault | the real 2xx / 4xx | the definite `Expect.That` |
| `before` | `500` | indefinite, `SameState()` |
| `after` | `500` | indefinite, success state |

ASGI gives an app no way to drop a connection before its response starts --
servers answer `500` for an app that raises -- so a fault answers `500`, with an
`X-Indefinite-Fault` header naming it. If the response had already started
(a streaming body), the middleware re-raises, and the server closes the
connection mid-response.

Each request gets its own injection, so the seed is its whole input: the same
seed on the same endpoint takes the same path at step 1 or step 100, under
concurrency. From the .NET client binding, send a **different seed per request**,
derived from one run seed (for concurrent test cases, from the test case and
step, not a counter). Things to know:

- The fault header is for debugging; the spec must not read it.
- A seed is an int64: exactly one `X-Indefinite-Seed` value of at most 19
  digits, optionally negative. Anything else -- malformed, out of range, or
  repeated -- gets a `400`, and the app never sees the request
  ([`spec/seed-header.tsv`](../../spec/seed-header.tsv)).
  Requests without the header, and lifespan and websocket scopes, pass
  through untouched.
- A task group wraps the fault in a `BaseExceptionGroup`; the middleware sees
  through it. Beside an ordinary exception the fault still ends the request;
  beside a teardown, the teardown wins.
- Installing the middleware twice raises `RuntimeError` on the first seeded
  request.
- Never install it in production: any caller could fault your server.

## Details and caveats

- Works on `def` and `async def`; generators are rejected.
- `@indefinite` must be the innermost decorator: put `@classmethod`,
  `@staticmethod`, or `@property` above it, not below.
- A site is named `module.qualname` by default, so two closures from one
  factory -- or one method on two instances -- share a fault stream. Name them
  with `@indefinite(name="...")` to give each its own.
- `name=` is required for lambdas (every lambda in a module would share one
  site) and lets you wrap any callable, such as a `functools.partial` or an
  object with `__call__`: `indefinite(name="db.commit")(session.commit)`.
- A site name goes on the fault line, so it must be printable, with no
  whitespace: `ValueError` otherwise.
- A sync function that returns an awaitable raises `TypeError` in a seeded
  request: when the call returns, the operation hasn't happened yet, so
  `after` would lie. Make it `async def`, or mark it with
  `inspect.markcoroutinefunction`.
- The injection follows `contextvars`: asyncio tasks the request creates and
  `asyncio.to_thread` see it. A plain thread or `loop.run_in_executor`
  doesn't, unless its work runs in `contextvars.copy_context().run(fn)` (or,
  on 3.14+, `threading.Thread(..., context=contextvars.copy_context())`;
  free-threaded builds pass the context by default). A call that doesn't see
  the injection is silently never faulted.
- When the request ends, its injection closes: a task that outlives it calls
  through with no faults.
- Within one request, calls to the same function are numbered in order. If
  concurrent tasks inside one request race on the same function, who gets
  which number is up to the scheduler.
