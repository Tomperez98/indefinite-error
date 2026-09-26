# indefinite-error for TypeScript

Inject **indefinite errors** ("it may or may not have happened") into the
requests your HTTP server handles. A marked call can end its request right
before or right after it runs, as if the request or its response were lost.
Then check that your retries can't apply a write twice.

```ts
import { indefinite, withIndefinite } from "indefinite-error";

// a boundary write whose outcome can get lost
const commit = indefinite("db.commit", (tx: Tx) => tx.commit());

// tests only: requests that carry X-Indefinite-Seed get faults
Bun.serve({ fetch: withIndefinite(app.fetch) });
```

On Express, or anything else built on Node's `http`, use the Node
middleware instead: [Express →](#express)

This is a port of the [Python package](../python). Every port is tested
against the same contract, [`spec/`](../spec), so a seed replays the same
faults in a TypeScript service, a Go one, a Rust one, and a Python one.

## Install

Tested on Bun 1.4, and on Node 22 and 24 (`bun run test:node`). It uses only
`AsyncLocalStorage` and fetch's `Request` and `Response`, which Deno and
Cloudflare Workers (with `nodejs_compat`) also provide, though they aren't
tested. Its one dependency is
[`@noble/hashes`](https://github.com/paulmillr/noble-hashes), for the BLAKE2b
the contract pins.

```sh
bun add indefinite-error   # or: npm install indefinite-error
```

## One call, three outcomes

Outside a request that carries a seed, a marked function just runs. Inside
one, each call either:

| Outcome | the function runs? | then |
|---|---|---|
| pass | yes | its real return value, exception, or rejection |
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

It writes that line to stderr, and the call throws (or rejects, if the
function is `async` or returned a promise). Your `finally` blocks run, and the
server and its other requests carry on. `withIndefinite` then answers `500`
with an `X-Indefinite-Fault` header naming the fault.

JavaScript has no exception a `catch` can't see, so a fault doesn't rely on
you rethrowing it. Once one fires, the request is over: every later marked
call in it throws the same fault without running, and `withIndefinite`
answers `500` whatever the handler returns. A retry loop, a catch-all, or a
framework's error handler can catch the fault, but can't undo it.

## Try it

The same `add(1)` under three seeds: one loses the write, one loses the
response, one is clean.

```ts
import { indefinite, withIndefinite } from "indefinite-error";

const ledger: number[] = [];

// the boundary write that can lose its outcome
const add = indefinite("ledger.add", (amount: number) => {
  ledger.push(amount);
});

// an ordinary fetch handler
const service = withIndefinite(async () => {
  add(1);
  return new Response("ok");
});

for (const seed of ["70", "74", "0"]) {
  ledger.length = 0;
  const request = new Request("http://bank/deposits", {
    method: "POST",
    headers: { "x-indefinite-seed": seed },
  });
  const { status } = await service(request);
  console.log(`seed=${seed}: status=${status}, ledger=${JSON.stringify(ledger)}`);
}
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

1. Mark each call whose outcome can get lost: database commits, calls to
   other services, messages you publish. Wrap a function with
   `indefinite(name, fn)`, which returns a function of the same type, or
   decorate a class method:

   ```ts
   class Store {
     @indefinite("store.deposit")
     async deposit(account: string, amount: number) { ... }
   }
   ```

2. Wrap your fetch handler with `withIndefinite`, behind a flag that is off
   in production. It takes any `(request, ...rest) => Response` handler:
   `Bun.serve({ fetch })`, `Deno.serve`, Hono's `app.fetch`, a Workers
   `fetch`. Put it outermost, so it sees the response last.
3. Send a different seed on every request, derived from one run seed. Treat a
   `500` as "may or may not have happened", and check your invariants
   afterwards. From .NET,
   [Accordant](https://microsoft.github.io/accordant/docs/how-to/indefinite-failures.html)
   drives the two branches for you.

## Express

`indefinite-error/node` has the same middleware for Node's `http`
`(req, res, next)` shape: Express, Connect, Polka, or a bare
`http.createServer`. It doesn't depend on Express.

```ts
import express from "express";
import { indefinite } from "indefinite-error";
import { indefiniteMiddleware } from "indefinite-error/node";

const commit = indefinite("db.commit", (tx: Tx) => tx.commit());

const app = express();
if (process.env.INDEFINITE_ERRORS === "1") {
  app.use(indefiniteMiddleware()); // first, and never in production
}
app.use(express.json());
app.post("/deposits", async (req, res) => {
  await commit(tx);
  res.json({ status: "ok" });
});
```

It keeps the same contract as `withIndefinite`, adapted to a response you
write to rather than return:

- **Mount it first,** before your body parsers and routes. Everything its
  `next()` leads to runs inside the injection, including after `await`s and
  a slow body through `express.json()`.
- **Whoever responds after a fault answers for it.** The route that caught
  it, Express's error handler, yours: the first `writeHead`, `write`, or
  `end` sends the fault's fresh `500` instead, and the lost response's later
  writes go nowhere (their callbacks still run).
- **Too late to answer.** If the headers had already gone out, a fault
  destroys the response as it fires, and the client sees the connection drop
  mid-response.
- **Use Express 5.** It sends a rejected `async` route to the error handler.
  Express 4 doesn't: there a fault in an `async` route is an unhandled
  rejection, as any error would be, unless you catch it or use
  `express-async-errors`.
- **Something must respond.** A route that swallows the fault and never
  responds hangs, as it would without faults.

## Example: a bank that must not double-count

[`examples/bank`](examples/bank) is a `bun:sqlite` bank, served both as a
fetch handler and as an Express app, with a retrying client. Under injection an unkeyed deposit double-counts, and the
fault lines say why; an idempotency key fixes it.
[Walkthrough →](examples/bank)

| | runs wrong (of 50) |
|---|---|
| unkeyed deposits | 39 — e.g. `balance 21, expected 20` |
| keyed deposits | 0 |

```sh
bun test ./examples/bank
```

## Details and caveats

- **Sites are names.** JavaScript function names don't survive minifiers or
  arrow functions, so every site is named explicitly. Each name draws its own
  faults, so calls to one site never shift another's; two marked functions
  with one name share one fault stream. A name must be non-empty and
  printable, with no whitespace, because it goes on the fault line:
  `indefinite` throws a `TypeError` otherwise.
- **Where a fault throws.** An `async` function's fault rejects. A plain
  function's throws: before it runs, or after it returns. If a plain function
  returns a promise, `after` waits for it to settle, then rejects; `before`
  throws, since the function never ran to return one. Under `await` these
  are the same.
- **A dropped fault is harmless.** A fault's rejection is marked handled, so
  a call nobody awaits doesn't crash the process with an unhandled
  rejection. The request still ends.
- **The injection follows `AsyncLocalStorage`.** Everything the request
  starts sees it: `await`s, `Promise.all`, timers, stream callbacks. A worker
  thread, or a call made outside the request (a queue consumer, a module-level
  promise), doesn't: it is never faulted, and nothing tells you so.
- **Too late to answer.** A seeded response's body streams inside the
  request. A fault while it streams errors the body, and the server drops the
  connection mid-response. The wrapper re-streams every seeded response, so
  those go out chunked.
- **A fault response is fresh.** It has an empty body, and none of the headers
  the lost response had. The `X-Indefinite-Fault` value is the payload's
  UTF-8 bytes, as the spec pins; read them back with
  `new TextDecoder().decode(Uint8Array.from(value, (c) => c.charCodeAt(0)))`.
- **Seeds are int64.** A seed header is exactly one value of at most 19
  digits, optionally negative. A malformed, out-of-range, or repeated one gets
  a `400`, and the handler never sees the request
  ([`spec/seed-header.tsv`](../spec/seed-header.tsv)). Seeds are `bigint`s,
  since a `number` can't hold every int64.
- **Installing the middleware twice** throws on the first seeded request.
- **After the request ends,** its injection closes: the response body has
  finished, errored, or been cancelled. A timer that outlives the request
  calls through with no faults.
- **Call numbers follow the order calls start.** Within one request, calls to
  one site are numbered in the order they're made. Concurrent calls are
  numbered in the order they start, not the order they settle.
- **Generators aren't supported**: when a generator "returns", it hasn't run.
  `indefinite` throws a `TypeError` for one.
- **Never install it in production.** Any caller could fault your server.

How seeds decide, and how this maps onto HTTP indefinite failures:
[How it works](../python/docs/how-it-works.md). The mechanics are the same;
where that page says `BaseException`, read "a fault the injection remembers".

## Development

```sh
bun install
bun run ci     # the merge gate: types, tests (100% line and function coverage), build
bun run check  # just types
bun run test:node  # the built package under Node: needs `node` on PATH
FC_SEED=random FC_RUNS=100000 bun test test/core.test.ts   # long property runs
```

The tests check this package against [`spec/`](../spec), the contract it
shares with the other ports: the fault schedule, the fault lines, and which
seed headers are accepted. A change that shifts the schedule breaks every
saved seed, in every language. `test/readme.test.ts` runs this README's
examples and checks that they print what it shows.
