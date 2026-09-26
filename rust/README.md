# indefinite-error for Rust

Inject **indefinite errors** ("it may or may not have happened") into the
requests your HTTP server handles. A marked call can end its request right
before or right after it runs, as if the request or its response were lost.
Then check that your retries can't apply a write twice.

```rust
use axum::{Router, routing::post};
use indefinite_error::{IndefiniteLayer, Site};

static COMMIT: Site = Site::new("db.commit"); // a boundary write whose outcome can get lost

async fn deposit() -> &'static str {
    COMMIT.run(async { /* tx.commit().await */ }).await;
    "ok"
}

let app: Router = Router::new()
    .route("/deposits", post(deposit))
    .layer(IndefiniteLayer::new()); // tests only: requests carrying X-Indefinite-Seed get faults
```

This is a port of the [Python package](../python). Every port is tested
against the same contract, [`spec/`](../spec), so a seed replays the same
faults in a Rust service, a Go one, and a Python one.

## Install

Requires Rust 1.86+. `IndefiniteLayer` is a [tower](https://docs.rs/tower)
layer, so it works with axum, hyper, and tonic. The other dependencies are
`blake2`, for the hash the contract pins, and `unicode-properties`, to check
site names.

```sh
cargo add --git https://github.com/Tomperez98/indefinite-error indefinite-error
```

## One call, three outcomes

Outside a request that carries a seed, `site.run(op)` is `op.await`. Inside
one, each call either:

| Outcome | `op` runs? | then |
|---|---|---|
| pass | yes | its real output, `Ok` or `Err` |
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

It writes that line to stderr. Then the request ends the way any Rust future
is cancelled: the call never returns, and the layer drops the request's
future and answers `500` with an `X-Indefinite-Fault` header naming the
fault. Destructors run on the way out, and the server and its other requests
carry on. The fault is not an `Err`, so no `?`, `match`, or retry loop can
see it.

## Try it

The same `add(1)` under three seeds: one loses the write, one loses the
response, one is clean.

```rust
use std::sync::Mutex;

use axum::{Router, body::Body, http::Request, routing::post};
use indefinite_error::{IndefiniteLayer, SEED_HEADER, Site};
use tower::ServiceExt; // for oneshot

static ADD: Site = Site::new("ledger.add"); // the boundary write that can lose its outcome
static LEDGER: Mutex<Vec<i32>> = Mutex::new(Vec::new());

async fn deposit() -> &'static str {
    ADD.run(async { LEDGER.lock().unwrap().push(1) }).await;
    "ok"
}

#[tokio::main]
async fn main() {
    let app = Router::new()
        .route("/deposits", post(deposit))
        .layer(IndefiniteLayer::new());

    for seed in ["70", "74", "0"] {
        LEDGER.lock().unwrap().clear();
        let request = Request::post("/deposits").header(SEED_HEADER, seed).body(Body::empty()).unwrap();
        let status = app.clone().oneshot(request).await.unwrap().status();
        println!("seed={seed}: status={}, ledger={:?}", status.as_u16(), LEDGER.lock().unwrap());
    }
}
```

```text
indefinite-error: before ledger.add#0 (seed=70)
indefinite-error: after ledger.add#0 (seed=74)
seed=70: status=500, ledger=[]
seed=74: status=500, ledger=[1]
seed=0: status=200, ledger=[1]
```

`seed=70` faults *before*, so the write never happened. `seed=74` faults
*after*: the write committed, so a client that retries on the `500` applies it
a second time. That is the bug this crate exists to find. `seed=0` runs clean.

## Use it on your service

1. Declare a `static` `Site` for each call whose outcome can get lost:
   database commits, calls to other services, messages you publish. Wrap the
   call in `SITE.run(...)`: `COMMIT.run(tx.commit()).await?`.
2. Install `IndefiniteLayer` behind a flag that is off in production.
3. Send a different seed on every request, derived from one run seed. Treat a
   `500` as "may or may not have happened", and check your invariants
   afterwards. From .NET,
   [Accordant](https://microsoft.github.io/accordant/docs/how-to/indefinite-failures.html)
   drives the two branches for you.

## Example: a bank that must not double-count

[`examples/bank`](examples/bank) is an axum bank with a retrying client. Under
injection an unkeyed deposit double-counts, and the fault lines say why; an
idempotency key fixes it. [Walkthrough →](examples/bank)

| | runs wrong (of 50) |
|---|---|
| unkeyed deposits | 39 — e.g. `balance 21, expected 20` |
| keyed deposits | 0 |

```sh
cd examples/bank && cargo test
```

## Details and caveats

- **Pass a lazy future.** `before` means `op` is dropped without being
  polled, so it must not start until it's polled: an `async` block, or a call
  to an `async fn`. A `JoinHandle` from `tokio::spawn` or `spawn_blocking` is
  already running, and `before` would lie. Spawn inside the block instead:
  `COMMIT.run(async { spawn_blocking(commit).await })`. For synchronous work,
  `COMMIT.run(async { tx.commit() })`.
- **The injection travels with the request's future.** Anything the request
  polls sees it: nested `async` blocks, `join!`, `select!`. A task spawned
  with `tokio::spawn`, or a thread, doesn't: a call made there is never
  faulted, and nothing tells you so.
- **A fault ends the whole request.** Once one fires, no later call in that
  request runs, including a sibling in the same `join!`. The layer drops the
  request's future before it answers.
- **A panic in `op` wins.** It propagates unchanged, and an `after` fault
  stays silent. An `Err` that `op` returns is an outcome: `after` discards it.
- **Too late to answer.** Once the inner service has returned its response,
  the layer can't answer `500`. A fault while the body streams makes the body
  yield an error instead, and the server drops the connection mid-response.
- **A fault response is fresh.** It has an empty body, and none of the headers
  the lost request set. Layers outside `IndefiniteLayer` still add theirs.
- **Sites are names.** Each name draws its own faults, so calls to one site
  never shift another's. Two `Site`s with the same name share one fault
  stream. A name must be non-empty and printable with no whitespace, because
  it goes on the fault line. In a `static`, an ASCII space or control
  character is a compile error; the rest of Unicode is checked on the first
  `run`, which panics on a bad name.
- **Seeds are `i64`.** A seed header is exactly one value of at most 19
  digits, optionally negative. A malformed, out-of-range, or repeated one gets
  a `400`, and the service never sees the request
  ([`spec/seed-header.tsv`](../spec/seed-header.tsv)).
- **Installing the layer twice** panics on the first seeded request.
- **After the request ends,** its injection closes: the response body has
  finished, or been dropped. A future that outlives the request calls through
  with no faults.
- **Call numbers follow the order calls start.** Within one request, calls to
  one site are numbered in the order they're first polled. If threads race on
  the same site, the scheduler decides who gets which number.
- **`cargo test` doesn't capture fault lines.** They go straight to stderr
  in one write, as the contract pins, not through `eprintln!`, so they show
  up even for tests that pass.
- **Never install it in production.** Any caller could fault your server.

How seeds decide, and how this maps onto HTTP indefinite failures:
[How it works](../python/docs/how-it-works.md). The mechanics are the same;
where that page says `BaseException`, read "the request's future is dropped".

## Development

```sh
cargo fmt --check && cargo clippy --all-targets -- -D warnings && cargo test
cargo +1.86 test                                     # the minimum supported Rust
PROPTEST_CASES=100000 cargo test --lib tests::core   # long property runs
```

Keep `cargo +1.86 test` in CI: clippy's MSRV lint checks standard-library
APIs, not language features such as let chains, so only an old toolchain
catches those.

The tests check this crate against [`spec/`](../spec), the contract it shares
with the other ports: the fault schedule, the fault lines, and which seed
headers are accepted. A change that shifts the schedule breaks every saved
seed, in every language. The spec tests pass vacuously in a downloaded crate,
which has no `spec/` beside it. In CI, set `INDEFINITE_ERROR_SPEC=required` so
a missing `spec/` fails instead.

The README is the crate's documentation, so `cargo test` runs its examples,
and `src/tests/readme.rs` checks that the output it shows is what they print.
