package indefinite

import (
	"context"
	"io"
	"maps"
	"math"
	"slices"
	"sync"
	"testing"
	"time"
)

// The injection a request carries in its context: scope, visibility, bookkeeping.

func TestAnyInt64IsASeed(t *testing.T) {
	// Negative, zero, and the extremes: all valid, all replayable.
	_, o := flavors[0].recorder("any")
	for _, seed := range []int64{math.MinInt64, -1, 0, 1, math.MaxInt64} {
		if a, b := run(t, seed, o, 20), run(t, seed, o, 20); !slices.Equal(a, b) {
			t.Fatalf("seed %d: %v != %v", seed, a, b)
		}
	}
}

// --- Scope ---

func TestANestedInjectionPanics(t *testing.T) {
	ctx, _ := request(t, 1)
	mustPanic(t, "a request seeded 2 inside one seeded 1: is indefinite.Handler installed twice?", func() {
		inject(ctx, 2, io.Discard)
	})
}

func TestAClosedInjectionInjectsNothing(t *testing.T) {
	// A goroutine that outlives its request calls through, and may start its own.
	ran, o := flavors[0].recorder("op")
	for seed := range int64(numSeeds) {
		ctx, in := inject(context.Background(), seed, io.Discard)
		in.close()
		*ran = (*ran)[:0]
		for i := range calls {
			if got, err := o(ctx, i); got != i*2 || err != nil {
				t.Fatalf("seed %d: op(%d) = %d, %v", seed, i, got, err)
			}
		}
		if len(*ran) != calls || len(in.snapshot()) != 0 {
			t.Fatalf("seed %d: ran %d, calls %v", seed, len(*ran), in.snapshot())
		}
		inner, next := inject(ctx, seed+1, io.Discard)
		if injectionFrom(inner) != next || next.seed != seed+1 {
			t.Fatalf("seed %d: the leaked goroutine can't start its own injection", seed)
		}
		next.close()
	}
}

func TestAContextWithoutTheInjectionIsASilentMiss(t *testing.T) {
	// The documented trap: context.Background() instead of the request's context.
	_, o := flavors[0].recorder("missed")
	_, in := request(t, 0)
	for i := range calls {
		attempt(t, context.Background(), o, i)
	}
	if len(in.snapshot()) != 0 {
		t.Fatalf("calls %v", in.snapshot())
	}
}

func TestDerivedContextsSeeTheInjection(t *testing.T) {
	// Timeouts, values, and WithoutCancel all keep the request's injection.
	_, o := flavors[0].recorder("derived")
	for seed := range int64(numSeeds) {
		want := run(t, seed, o, calls)
		ctx, in := inject(context.Background(), seed, io.Discard)
		ctx, cancel := context.WithTimeout(context.WithoutCancel(ctx), time.Hour)
		ctx = context.WithValue(ctx, struct{ k int }{1}, "v")
		var got []string
		for i := range calls {
			got = append(got, attempt(t, ctx, o, i))
		}
		cancel()
		in.close()
		if !slices.Equal(got, want) {
			t.Fatalf("seed %d: %v != %v", seed, got, want)
		}
	}
}

// --- Goroutines ---

func TestAGoroutineGivenTheContextSeesTheInjection(t *testing.T) {
	_, o := flavors[0].recorder("spawned")
	for seed := range int64(numSeeds) {
		want := run(t, seed, o, calls)
		ctx, in := inject(context.Background(), seed, io.Discard)
		var got []string
		var wg sync.WaitGroup
		wg.Go(func() {
			for i := range calls {
				got = append(got, attempt(t, ctx, o, i))
			}
		})
		wg.Wait()
		in.close()
		if !slices.Equal(got, want) {
			t.Fatalf("seed %d: %v != %v", seed, got, want)
		}
	}
}

func TestGoroutinesSharingOneInjectionLoseNoCalls(t *testing.T) {
	// Every concurrent call gets its own n, and the faults are exactly the seed's.
	// Run it with -race.
	const site, goroutines, perGoroutine = "hot", 8, 5_000
	s := NewSite(site)
	seed := seedWhere(t, site, modeBoth)
	ctx, in := request(t, seed)

	var mu sync.Mutex
	var faulted []int
	var wg sync.WaitGroup
	start := make(chan struct{})
	for range goroutines {
		wg.Go(func() {
			<-start
			for range perGoroutine {
				if f, ok := catch(func() { _ = s.Do(ctx, func() error { return nil }) }); ok {
					mu.Lock()
					faulted = append(faulted, f.n)
					mu.Unlock()
				}
			}
		})
	}
	close(start)
	wg.Wait()

	total := goroutines * perGoroutine
	if got := in.snapshot(); !maps.Equal(got, map[string]int{site: total}) {
		t.Fatalf("calls %v, want %d", got, total)
	}
	var want []int
	for n := range total {
		if decide(seed, in.rate, modeBoth, site, n) != pass {
			want = append(want, n)
		}
	}
	slices.Sort(faulted)
	if !slices.Equal(faulted, want) {
		t.Fatalf("faulted %d calls, want %d", len(faulted), len(want))
	}
}

func TestConcurrentRequestsDoNotInterfere(t *testing.T) {
	// One injection per request, all overlapping: each gets the path it gets alone.
	_, o := flavors[0].recorder("op")
	alone := make([][]string, numSeeds)
	for seed := range numSeeds {
		alone[seed] = run(t, int64(seed), o, calls)
	}
	s := NewSite("op")
	got := make([][]string, numSeeds)
	var wg sync.WaitGroup
	for seed := range numSeeds {
		wg.Go(func() {
			ctx, in := inject(context.Background(), int64(seed), io.Discard)
			defer in.close()
			for i := range calls {
				f, faulted := catch(func() { _, _ = Call(ctx, s, func() (int, error) { return i * 2, nil }) })
				outcome := "ok"
				if faulted {
					outcome = f.phase.String()
				}
				got[seed] = append(got[seed], outcome)
			}
		})
	}
	wg.Wait()
	for seed := range numSeeds {
		if !slices.Equal(got[seed], alone[seed]) {
			t.Fatalf("seed %d: %v != %v", seed, got[seed], alone[seed])
		}
	}
}
