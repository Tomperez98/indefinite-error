package indefinite

import (
	"context"
	"fmt"
	"io"
	"maps"
	"sync"
)

// fault is one injected fault: the n-th call to site in the request seeded seed.
type fault struct {
	seed  int64
	site  string
	n     int
	phase phase
}

// String is the fault line's payload: "after app.get#4 (seed=13)".
func (f fault) String() string {
	return fmt.Sprintf("%s %s#%d (seed=%d)", f.phase, f.site, f.n, f.seed)
}

// abort is the panic value that ends a faulted request. It is unexported, so
// only Handler can recover it; it isn't an error, so no error path sees it.
type abort struct{ fault fault }

// String is what the runtime prints if the panic escapes its request: a fault
// raised on a goroutine the handler started, and never carried back.
func (a *abort) String() string {
	return "indefinite-error: " + a.fault.String() +
		": a fault escaped its request; only indefinite.Handler may recover it"
}

// injection is one request's injection: what its seed chose, and which sites
// it reached. The only state is a call counter per site. Once its request
// ends it is closed: a goroutine that outlives the request calls through.
type injection struct {
	seed int64
	rate float64
	log  io.Writer // where fault lines go: stderr, outside tests

	mu     sync.Mutex
	calls  map[string]int
	modes  map[string]mode // a cache of modeOf(seed, site)
	closed bool
}

type ctxKey struct{}

// inject returns a child of ctx carrying a new injection seeded seed. The
// caller must close it when the request ends.
func inject(ctx context.Context, seed int64, log io.Writer) (context.Context, *injection) {
	if active := injectionFrom(ctx); active != nil && !active.isClosed() {
		panic(fmt.Sprintf(
			"indefinite: a request seeded %d inside one seeded %d: is indefinite.Handler installed twice?",
			seed, active.seed))
	}
	in := &injection{
		seed:  seed,
		rate:  rateOf(seed),
		log:   log,
		calls: map[string]int{},
		modes: map[string]mode{},
	}
	return context.WithValue(ctx, ctxKey{}, in), in
}

func injectionFrom(ctx context.Context) *injection {
	in, _ := ctx.Value(ctxKey{}).(*injection)
	return in
}

// next counts one call to site, and reports whether it faults.
func (in *injection) next(site string) (fault, bool) {
	in.mu.Lock()
	defer in.mu.Unlock()
	if in.closed {
		return fault{}, false
	}
	n := in.calls[site]
	in.calls[site] = n + 1
	m, ok := in.modes[site]
	if !ok {
		m = modeOf(in.seed, site)
		in.modes[site] = m
	}
	p := decide(in.seed, in.rate, m, site, n)
	return fault{seed: in.seed, site: site, n: n, phase: p}, p != pass
}

// abort writes the fault line and returns the value to panic with.
// One Write per line, so lines from concurrent requests don't interleave.
func (in *injection) abort(f fault) *abort {
	_, _ = io.WriteString(in.log, "indefinite-error: "+f.String()+"\n") // a lost line: the response still names it
	return &abort{fault: f}
}

func (in *injection) close() {
	in.mu.Lock()
	defer in.mu.Unlock()
	in.closed = true
}

func (in *injection) isClosed() bool {
	in.mu.Lock()
	defer in.mu.Unlock()
	return in.closed
}

// snapshot is the calls per site so far, for tests. A missing site was never reached.
func (in *injection) snapshot() map[string]int {
	in.mu.Lock()
	defer in.mu.Unlock()
	return maps.Clone(in.calls)
}
