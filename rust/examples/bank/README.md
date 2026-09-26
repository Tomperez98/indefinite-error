# Example: a bank that must not double-count

A client that times out doesn't know whether its deposit happened, so it
retries. If the deposit did happen, the retry adds the money twice.
`indefinite-error` reproduces that "may or may not have happened" gap, and this
example uses it to find the double-count in a tiny axum service — then shows
the idempotency key that fixes it.

```sh
cd examples/bank && cargo test
```

Two tests tell the whole story:

| test | result |
|---|---|
| unkeyed deposits | 39 of 50 runs end wrong, e.g. `balance 21, expected 20` |
| keyed deposits | all 50 runs land exactly on 20 |

Both tests run the same 20 deposits under injected faults, retrying like a real
client; the only difference is whether each request carries an idempotency key.

Because every port shares the same [`spec/`](../../../spec) schedule, these are
the *same* runs as the [Python example](../../../python/examples/bank): run 1
fails here with the very faults and balance the Python README quotes.

## 1. Mark the writes whose outcome can be lost

[`src/lib.rs`](src/lib.rs) is an ordinary axum app over an in-memory `Store`.
Its writes run under a `Site`: a fault lands right **before** the write (it
never happened) or right **after** it (it committed, but the response is
lost).

```rust
static DEPOSIT: Site = Site::new("store.deposit");

pub async fn deposit(&self, account: &str, amount: i64) {
    DEPOSIT.run(async {
        self.inner.lock().unwrap().add(account, amount);
    }).await;
}
```

There are two endpoints. `POST /deposits/unkeyed` adds the amount;
`POST /deposits` takes a `key` and applies each key once, in the same critical
section as the money.

```rust
static DEPOSIT_ONCE: Site = Site::new("store.deposit_once");

pub async fn deposit_once(&self, key: &str, account: &str, amount: i64) {
    DEPOSIT_ONCE.run(async {
        let mut inner = self.inner.lock().unwrap();
        // one critical section: the key and the money, or neither
        if inner.applied.insert(key.to_owned()) {
            inner.add(account, amount);
        }
    }).await;
}
```

The `Store` is in memory so the example has no database dependency; a real
service marks its database commits, calls to other services, and messages it
publishes the same way.

## 2. Install the layer only when testing

```rust
if indefinite_errors {  // never in production: any caller could fault the server
    router.layer(IndefiniteLayer::new())
} else {
    router
}
```

Requests that carry an `X-Indefinite-Seed` header get faults; others run
normally. A faulted request gets a `500` with an `X-Indefinite-Fault` header,
and the layer writes one line per fault to stderr.

## 3. Watch it by hand

Run with faults on:

```sh
INDEFINITE_ERRORS=1 cargo run
```

Send the same deposit three times with three seeds, as a client retrying a
failed request would:

```sh
D='{"key": "deposit-1", "account": "alice", "amount": 1}'
curl -i localhost:8000/deposits -H 'content-type: application/json' -H 'x-indefinite-seed: 3' -d "$D"
curl -i localhost:8000/deposits -H 'content-type: application/json' -H 'x-indefinite-seed: 1' -d "$D"
curl -i localhost:8000/deposits -H 'content-type: application/json' -H 'x-indefinite-seed: 0' -d "$D"
curl localhost:8000/accounts/alice
```

```text
HTTP/1.1 500 Internal Server Error
x-indefinite-fault: before store.deposit_once#0 (seed=3)

HTTP/1.1 500 Internal Server Error
x-indefinite-fault: after store.deposit_once#0 (seed=1)

HTTP/1.1 200 OK
{"status":"ok"}

{"balance":1}
```

Seed 3 faulted *before* the write, so nothing happened. Seed 1 faulted *after*
it: the deposit committed, but the client only saw a `500`. The retry with seed
0 went through, and the key stopped it from counting twice. The server log
names each fault, so a CI log is enough to replay it:

```text
indefinite-error: before store.deposit_once#0 (seed=3)
indefinite-error: after store.deposit_once#0 (seed=1)
```

Send seed 1 again and it faults *after* again: the same seed always takes the
same path.

## 4. Let a test judge the result

[`tests/bank.rs`](tests/bank.rs) makes 20 deposits of 1 per run, and gives every
request its own seed derived from the run, step, and attempt. On a `500` it
retries, as a real client would, and at the end it checks one invariant: the
balance is exactly 20.

```rust
for attempt in 0..ATTEMPTS {
    let (status, fault) = call(&app, "POST", path, &body(step), seed(run, step, attempt)).await;
    if status == StatusCode::OK {
        break;
    }
    assert_eq!(status, StatusCode::INTERNAL_SERVER_ERROR); // indefinite: retry
    result.faults.push(fault.expect("a 500 names its fault"));
}
```

With keys, every run is exact. Without them, 39 of the 50 runs are wrong, and
the faults the server reported say why. Run 1 has one fault:

```text
balance 21, expected 20; faults: ["after store.deposit#0 (seed=1004000)"]
```

The first deposit committed, its response was lost, and the retry added it
again. Because each request's seed comes from the run, rerunning run 1 replays
exactly this.

The layer writes its fault lines straight to stderr, which `cargo test` doesn't
capture, so a passing run still prints them — that is normal, and they are what
a failing run is read against.

## Using it on your own service

1. Declare a `static Site` for each call whose outcome can get lost: database
   commits, calls to other services, messages you publish. Wrap the call in
   `SITE.run(...)`.
2. Install `IndefiniteLayer` behind a flag that is off in production.
3. In your tests, or from a client such as
   [Accordant](https://microsoft.github.io/accordant/docs/how-to/indefinite-failures.html),
   send a different seed on every request, derived from one run seed. Treat the
   `500` as "may or may not have happened", and check your invariants
   afterwards.