# indefinite-error

Reproducibly inject **indefinite errors** ("it may or may not have happened")
into the requests your server handles: end a request right before or right
after a call at your system's boundary, as if the request or its response
were lost.

```python
from indefinite_error import indefinite
from indefinite_error.asgi import IndefiniteMiddleware


@indefinite
def commit(tx) -> None:
    tx.commit()


app = IndefiniteMiddleware(app)  # requests carrying X-Indefinite-Seed get faults
```

Requires Python 3.12+. No dependencies. For a walkthrough -- a FastAPI
service, a retrying client, and the double-counting bug it finds -- see
[`examples/bank`](examples/bank).

Outside a request that carries a seed, the decorator does nothing. Inside, each
call either:

| Outcome | `commit` runs? | Then |
|---|---|---|
| pass | yes | the real return value, or the real exception |
| `before` | no | the request ends: it never happened |
| `after` | yes, it returned or raised | the request ends: it happened, nobody was told |

A teardown `commit` raises -- `KeyboardInterrupt`, `SystemExit`,
`asyncio.CancelledError` -- outranks the fault and propagates unchanged.

## A fault ends the request

Every fault first writes one line to stderr:

```text
indefinite-error: after app.get#4 (seed=13)
```

It names the phase, the site, the call number, and the seed, so a CI log is
enough to replay it. Then a private exception unwinds the request to the
middleware, which answers for it. Cleanup runs -- `finally` blocks, `with`
exits, lock releases -- as it would when a request is cancelled. The server
and its other requests carry on, and its memory survives.

The exception is a `BaseException`, so `except Exception` handlers and retry
loops don't see it, and it isn't exported, so nothing else can name it to
catch it. Only code that catches `BaseException` without re-raising would
swallow it -- as it would swallow a cancellation.

The same design carries to runtimes that can abort one request:
`panic(http.ErrAbortHandler)` in Go, a panic caught at the edge by axum's
`CatchPanicLayer` in Rust.

## The seed is the only knob

Each seed picks a fault rate and, per function, which phases may fault (`off`,
`before`, `after`, `both`), so sweeping seeds covers gentle and brutal runs
alike (swarm testing). Every decision is a hash of
`(seed, function, call number)`, counted within the request: the same seed
replays the same faults, in any process, and calls to one function never shift
another's faults.

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
servers answer `500` for an app that raises -- so a fault answers `500`, with
an `X-Indefinite-Fault` header naming it. If the response had already started
(a streaming body), the middleware re-raises, and the server closes the
connection mid-response.

Each request gets its own injection, so the seed is its whole input: the same
seed on the same endpoint takes the same path at step 1 or step 100, under
concurrency. From the .NET client binding, send a **different seed per
request**, derived from one run seed (for concurrent test cases, from the test
case and step, not a counter). Things to know:

- The fault header is for debugging; the spec must not read it.
- A malformed seed header gets a `400`; the app never sees the request.
  Requests without the header, and lifespan and websocket scopes, pass through
  untouched.
- A task group wraps the fault in a `BaseExceptionGroup`; the middleware sees
  through it. Beside an ordinary exception the fault still ends the request;
  beside a teardown, the teardown wins.
- Installing the middleware twice raises `RuntimeError` on the first seeded
  request.
- Never install it in production: any caller could fault your server.

## Details

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

## Development

```sh
./ci          # the merge gate: format, lint, types, tests (100% branch coverage), build
./ci fast     # just format, lint, and types
./ci nightly  # long property runs, then mutation testing: every mutant must be killed
./ci stress   # the thread stress test, 50 times; run it on free-threaded 3.14t
```

CI runs `./ci` on 3.12, 3.13, 3.14, and 3.14t. `tests/golden/schedule.txt`
pins the fault schedule, so a change that would shift every saved seed fails
loudly; regenerate it only on purpose, with
`uv run python -m tests.test_core --regen`.
