# indefinite-error for Go

Inject **indefinite errors** ("it may or may not have happened") into the
requests your HTTP server handles. A marked call can end its request right
before or right after it runs, as if the request or its response were lost.
Then check that your retries can't apply a write twice.

```go
import "github.com/Tomperez98/indefinite-error/go/indefinite"

var commit = indefinite.NewSite("db.commit") // a boundary write whose outcome can get lost

func save(ctx context.Context, tx *sql.Tx) error {
	return commit.Do(ctx, tx.Commit)
}

h = indefinite.Handler(h) // tests only: requests that carry X-Indefinite-Seed get faults
```

This is a port of the [Python package](../python). Both are tested against
the same contract, [`spec/`](../spec), so a seed replays the same faults in a
Go service and a Python one.

## Install

Requires Go 1.26+. Its one dependency is `golang.org/x/crypto`, for BLAKE2b.

```sh
go get github.com/Tomperez98/indefinite-error/go
```

## One call, three outcomes

Outside a request that carries a seed, `Do` and `Call` just run the function.
Inside one, each call either:

| Outcome | the function runs? | then |
|---|---|---|
| pass | yes | its real return values |
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

It writes that line to stderr. Then it panics with a value that only
`Handler` recovers, and `Handler` answers `500` with an `X-Indefinite-Fault`
header naming the fault. Deferred calls run, and the server and its other
requests carry on. The panic isn't an `error`, so no `if err != nil` and no
retry loop can see it.

## Try it

The same `add(1)` under three seeds: one loses the write, one loses the
response, one is clean. This is [`Example`](indefinite/example_test.go), run by
`go test`.

```go
var ledger []int
add := indefinite.NewSite("ledger.add")

service := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
	_ = add.Do(r.Context(), func() error {
		ledger = append(ledger, 1)
		return nil
	})
	_, _ = io.WriteString(w, "ok")
})
h := indefinite.Handler(service)

for _, seed := range []string{"70", "74", "0"} {
	ledger = nil
	r := httptest.NewRequest(http.MethodPost, "/deposits", nil)
	r.Header.Set(indefinite.SeedHeader, seed)
	w := httptest.NewRecorder()
	h.ServeHTTP(w, r)
	fmt.Printf("seed=%s: status=%d, ledger=%v\n", seed, w.Code, ledger)
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
a second time. That is the bug this package exists to find. `seed=0` runs
clean.

## Use it on your service

1. Declare a `Site` for each call whose outcome can get lost: database
   commits, calls to other services, messages you publish. Wrap the call in
   `site.Do(ctx, fn)`, or in `indefinite.Call(ctx, site, fn)` when it returns
   a value.
2. Install `indefinite.Handler` behind a flag that is off in production.
   Put it inside any middleware that recovers panics, so it sees its faults
   first.
3. Send a different seed on every request, derived from one run seed. Treat a
   `500` as "may or may not have happened", and check your invariants
   afterwards. From .NET,
   [Accordant](https://microsoft.github.io/accordant/docs/how-to/indefinite-failures.html)
   drives the two branches for you.

## Details and caveats

- **Pass the request's context.** The injection travels in `r.Context()` and
  every context derived from it. A call given `context.Background()` is never
  faulted, and nothing tells you so.
- **Faults panic the calling goroutine.** `Handler` recovers them on the
  goroutine that serves the request. On a goroutine the handler started, a
  fault crashes the process, like any panic there, unless that goroutine
  recovers it and hands it back to be re-panicked. `errgroup` doesn't
  propagate panics.
- **A panic from the function wins.** If `fn` panics or calls
  `runtime.Goexit`, that propagates unchanged and an `after` fault stays
  silent. An error `fn` returns is an outcome: `after` discards it.
- **Too late to answer.** Once the response has started (`WriteHeader`,
  `Write`, `Flush`, or `Hijack`), `Handler` can't answer `500`. It panics with
  `http.ErrAbortHandler`, and the server drops the connection mid-response.
  The `ResponseWriter` it hands your handler supports `http.Flusher`,
  `http.Hijacker`, and `http.ResponseController`.
- **A fault response is fresh.** It has an empty body, and none of the headers
  the lost request set. Headers set by middleware outside `Handler` are kept.
- **Sites are names.** Each name draws its own faults, so calls to one site
  never shift another's. Two `Site`s with the same name share one fault
  stream. `NewSite` panics on a name that is empty, or that isn't printable
  UTF-8 without whitespace, because it goes on the fault line.
- **Seeds are `int64`.** A seed header is exactly one value of at most 19
  digits, optionally negative. A malformed, out-of-range, or repeated one gets
  a `400`, and the handler never sees the request
  ([`spec/seed-header.tsv`](../spec/seed-header.tsv)).
- **Installing `Handler` twice** panics on the first seeded request.
- **After the request ends,** its injection closes. A goroutine that outlives
  the request calls through with no faults.
- **Call numbers follow the order calls happen.** Within one request, calls to
  one site are numbered in order. If goroutines race on the same site, the
  scheduler decides who gets which number.
- **Never install it in production.** Any caller could fault your server.

How seeds decide, and how this maps onto HTTP indefinite failures:
[How it works](../python/docs/how-it-works.md). The mechanics are the same;
where that page says `BaseException`, read `panic`.

## Development

```sh
go vet ./... && go test ./... && go test -race ./...
go test -run '^$' -fuzz FuzzInjectionMatchesAReferenceModel -fuzztime 30s ./indefinite
```

Run the tests both with and without `-race`. The race detector slows
goroutines down, which can hide timing bugs that a plain `go test` hits.

The tests check this package against [`spec/`](../spec), the contract it
shares with the Python package: the fault schedule, the fault lines, and which
seed headers are accepted. A change that shifts the schedule breaks every saved
seed, in both languages. The spec tests skip in a downloaded module, which has
no `spec/` beside it. In CI, set `INDEFINITE_ERROR_SPEC=required` so a missing
`spec/` fails instead.
