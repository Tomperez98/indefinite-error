# indefinite-error

Inject **indefinite errors** — "it may or may not have happened" — into the
requests your server handles: end a request right before or right after a call
at your system's boundary, as if the request or its response were lost. Then
check that your retries can't double-apply a write.

```python
from indefinite_error import indefinite
from indefinite_error.asgi import IndefiniteMiddleware


@indefinite  # mark the boundary write that can lose its outcome
def commit(tx) -> None:
    tx.commit()


app = IndefiniteMiddleware(app)  # tests only: requests carrying X-Indefinite-Seed get faults
```

## Install

Requires Python 3.12+. No dependencies.

```sh
pip install indefinite-error   # or: uv add indefinite-error
```

## One call, three outcomes

Outside a request that carries a seed, `@indefinite` does nothing. Inside one,
each marked call either:

| Outcome | the call runs? | then |
|---|---|---|
| pass | yes | the real return value, or the real exception |
| `before` | no | the request ends: it never happened |
| `after` | yes | the request ends: it happened, but nobody was told |

`before` and `after` are exactly the two ways a write can be indefinite — state
unchanged, or state changed but the response lost — so a retrying client faces
the same ambiguity it would in production.

A fault names the phase, the site, the call number, and the seed, so a CI log is
enough to replay it:

```text
indefinite-error: after app.get#4 (seed=13)
```

It writes that line to stderr and ends only its own request: cleanup runs, the
server and its other requests carry on. The seed is the only knob, so the same
seed replays the same faults in any process. [How it works →](docs/how-it-works.md)

## Try it: one file, no dependencies

The same `add(1)` under three seeds: one loses the write, one loses the
response, one is clean.

```python
import asyncio

from indefinite_error import indefinite
from indefinite_error.asgi import IndefiniteMiddleware

ledger: list[int] = []


@indefinite(name="ledger.add")  # the boundary write that can lose its outcome
def add(amount: int) -> None:
    ledger.append(amount)


async def service(scope, receive, send):  # an ordinary ASGI app
    add(1)
    await send({"type": "http.response.start", "status": 200, "headers": []})
    await send({"type": "http.response.body", "body": b"ok"})


async def request(seed: int) -> int:
    status = 0

    async def send(message):
        nonlocal status
        if message["type"] == "http.response.start":
            status = message["status"]

    scope = {"type": "http", "headers": [(b"x-indefinite-seed", str(seed).encode())]}
    await IndefiniteMiddleware(service)(scope, receive=None, send=send)
    return status


for seed in (70, 74, 0):
    ledger.clear()
    print(f"seed={seed}: status={asyncio.run(request(seed))}, ledger={ledger}")
```

```text
indefinite-error: before ledger.add#0 (seed=70)
indefinite-error: after ledger.add#0 (seed=74)
seed=70: status=500, ledger=[]
seed=74: status=500, ledger=[1]
seed=0: status=200, ledger=[1]
```

`seed=70` faults *before*: the write never happened. `seed=74` faults *after*:
the write committed, so a client that retries on the `500` applies it a second
time — the bug this library exists to find. `seed=0` runs clean.

## Use it on your service

1. Put `@indefinite` on the calls whose outcome can get lost: database commits,
   calls to other services, messages you publish.
2. Install `IndefiniteMiddleware` behind a flag that is off in production.
3. Send a different seed on every request, derived from one run seed. Treat a
   `500` as "may or may not have happened", and check your invariants afterwards.
   From .NET, [Accordant](https://microsoft.github.io/accordant/docs/how-to/indefinite-failures.html)
   drives the two branches for you.

## Example: a bank that must not double-count

[`examples/bank`](examples/bank) is a FastAPI + SQLite bank with a retrying
client. Under injection an unkeyed deposit double-counts, and the fault lines
say why; an idempotency key fixes it. [Walkthrough →](examples/bank)

| | runs wrong (of 50) |
|---|---|
| unkeyed deposits | 39 — e.g. `balance 21, expected 20` |
| keyed deposits | 0 |

```sh
cd examples/bank && uv run pytest
```

## Development

```sh
./ci          # the merge gate: format, lint, types, tests (100% branch coverage), build
./ci fast     # just format, lint, and types
./ci nightly  # long property runs, then mutation testing: every mutant must be killed
./ci stress   # the thread stress test, 50 times; run it on free-threaded 3.14t
```

CI runs `./ci` on 3.12, 3.13, 3.14, and 3.14t. The tests check this package
against [`spec/`](../spec), the contract it shares with the [Go](../go) and [Rust](../rust) ports:
the fault schedule, the fault lines, and which seed headers are accepted. A
change that would shift every saved seed fails loudly, here, in Go, and in Rust;
regenerate `spec/schedule.tsv` only on purpose, with
`uv run python -m tests.test_core --regen`. Until the package is on PyPI, run
from a clone with `uv sync`.

## Examples

`examples/` holds separate [uv workspace](https://docs.astral.sh/uv/concepts/projects/workspaces/)
members. Each is its own project with its own dependencies, resolved with the
library into one lockfile, and the root `dev` group depends on them, so
`uv run pytest` at the root runs the example tests too. To run one alone, from
its own directory:

```sh
cd examples/bank && uv run pytest
```
