package indefinite

import (
	"context"
	"errors"
	"io"
	"maps"
	"runtime"
	"slices"
	"strings"
	"testing"
)

// The contract every site call keeps, through Call or through Site.Do. A test
// recovering the abort (catch) stands in for Handler; http_test.go has the real one.

// --- Before never ran; after ran; outside a request nothing happens ---

func TestInertOutsideARequest(t *testing.T) {
	eachFlavor(t, func(t *testing.T, fl flavor) {
		ran, o := fl.recorder("op")
		for i := range calls {
			if got, err := o(context.Background(), i); got != i*2 || err != nil {
				t.Fatalf("op(%d) = %d, %v", i, got, err)
			}
		}
		if len(*ran) != calls {
			t.Fatalf("ran %d of %d calls", len(*ran), calls)
		}
	})
}

func TestBeforeNeverRunsAfterAlwaysRuns(t *testing.T) {
	eachFlavor(t, func(t *testing.T, fl flavor) {
		for seed := range int64(numSeeds) {
			ran, o := fl.recorder("op")
			var want []int
			for i, outcome := range run(t, seed, o, calls) {
				if outcome != "before" {
					want = append(want, i)
				}
			}
			if !slices.Equal(*ran, want) {
				t.Fatalf("seed %d: ran %v, want %v", seed, *ran, want)
			}
		}
	})
}

// --- Definite outcomes pass through; a panic outranks the fault ---

func TestADefiniteErrorPassesThroughUnlessFaulted(t *testing.T) {
	eachFlavor(t, func(t *testing.T, fl flavor) {
		s := NewSite("boom")
		seen := map[string]bool{}
		for seed := range int64(numSeeds) {
			ctx, in := inject(context.Background(), seed, io.Discard)
			f, faulted := catch(func() {
				_, err := fl.call(ctx, s, func() (int, error) { return 0, errDefinite })
				if !errors.Is(err, errDefinite) {
					t.Fatalf("seed %d: err = %v, want the definite error", seed, err)
				}
				seen["definite"] = true
			})
			in.close()
			if faulted {
				seen[f.phase.String()] = true
			}
		}
		if want := []string{"after", "before", "definite"}; !slices.Equal(slices.Sorted(maps.Keys(seen)), want) {
			t.Fatalf("outcomes %v, want %v", seen, want)
		}
	})
}

func TestCallReturnsWhatFnReturned(t *testing.T) {
	s := NewSite("value")
	for seed := range int64(numSeeds) {
		ctx, in := request(t, seed)
		_, _ = catch(func() {
			got, err := Call(ctx, s, func() (string, error) { return "partial", errDefinite })
			if got != "partial" || err != errDefinite {
				t.Fatalf("seed %d: Call = %q, %v", seed, got, err)
			}
		})
		in.close()
	}
}

type fatal struct{}

func TestAPanicOutranksAfter(t *testing.T) {
	// A panic isn't an outcome: it propagates unchanged, and AFTER stays silent.
	eachFlavor(t, func(t *testing.T, fl flavor) {
		s := NewSite("panics")
		seen := map[string]bool{}
		log := &syncBuffer{}
		for seed := range int64(numSeeds) {
			ctx, in := inject(context.Background(), seed, log)
			func() {
				defer func() {
					switch v := recover().(type) {
					case *abort:
						seen[v.fault.phase.String()] = true
					case fatal:
						seen["fatal"] = true
					default:
						t.Fatalf("recovered %v", v)
					}
				}()
				_, _ = fl.call(ctx, s, func() (int, error) { panic(fatal{}) })
			}()
			in.close()
		}
		if want := []string{"before", "fatal"}; !slices.Equal(slices.Sorted(maps.Keys(seen)), want) {
			t.Fatalf("outcomes %v, want %v", seen, want)
		}
		if strings.Contains(log.String(), "after") {
			t.Fatalf("an after fault wrote its line although fn panicked:\n%s", log)
		}
	})
}

func TestGoexitOutranksAfter(t *testing.T) {
	s := NewSite("exits")
	var seed int64
	for modeOf(seed, "exits") != modeAfter || rateOf(seed) != 0.5 {
		seed++
	}
	log := &syncBuffer{}
	ctx, in := inject(context.Background(), seed, log)
	defer in.close()
	var drawn int
	for n := range calls {
		if decide(seed, in.rate, modeAfter, "exits", n) == after {
			drawn++
		}
	}
	if drawn == 0 {
		t.Fatalf("seed %d draws no after fault in %d calls", seed, calls)
	}
	for range calls {
		done := make(chan fault, 1)
		go func() {
			defer close(done)
			f, faulted := catch(func() { _ = s.Do(ctx, func() error { runtime.Goexit(); return nil }) })
			if faulted {
				done <- f // unreachable if Goexit won
			}
		}()
		if f, faulted := <-done; faulted {
			t.Fatalf("Goexit lost to %v", f)
		}
	}
	if log.String() != "" {
		t.Fatalf("an after fault wrote its line although fn exited:\n%s", log)
	}
}

// --- The fault unwinds the request ---

func TestTheFaultIsInvisibleToErrorChecks(t *testing.T) {
	// A retry loop can't retry past it: nothing after the fault runs.
	eachFlavor(t, func(t *testing.T, fl flavor) {
		_, o := fl.recorder("op")
		var faulted int
		for seed := range int64(numSeeds) {
			ctx, in := inject(context.Background(), seed, io.Discard)
			var attempts []int
			handler := func() string {
				for n := range 3 {
					attempts = append(attempts, n)
					if _, err := o(ctx, n); err != nil {
						continue
					}
					return "done"
				}
				return "gave up"
			}
			if _, f := catch(func() { handler() }); f {
				faulted++
			}
			in.close()
			if !slices.Equal(attempts, []int{0}) {
				t.Fatalf("seed %d: attempts %v; the loop must never reach a second attempt", seed, attempts)
			}
		}
		if faulted == 0 {
			t.Fatal("no seed faulted")
		}
	})
}

func TestAFaultRunsDeferredCalls(t *testing.T) {
	// The server lives on, so deferred calls run and locks are released.
	eachFlavor(t, func(t *testing.T, fl flavor) {
		s := NewSite("commit")
		want := map[string][]string{
			"ok":     {"commit", "after", "deferred"},
			"before": {"deferred"},
			"after":  {"commit", "deferred"},
		}
		for seed := range int64(numSeeds) {
			ctx, in := inject(context.Background(), seed, io.Discard)
			var events []string
			f, faulted := catch(func() {
				defer func() { events = append(events, "deferred") }()
				_, _ = fl.call(ctx, s, func() (int, error) {
					events = append(events, "commit")
					return 0, nil
				})
				events = append(events, "after")
			})
			in.close()
			outcome := "ok"
			if faulted {
				outcome = f.phase.String()
			}
			if !slices.Equal(events, want[outcome]) {
				t.Fatalf("seed %d (%s): events %v, want %v", seed, outcome, events, want[outcome])
			}
		}
	})
}

func TestAFaultWritesTheLine(t *testing.T) {
	eachFlavor(t, func(t *testing.T, fl flavor) {
		_, o := fl.recorder("logged")
		log := &syncBuffer{}
		var want strings.Builder
		for seed := range int64(numSeeds) {
			ctx, in := inject(context.Background(), seed, log)
			if f, faulted := catch(func() { _, _ = o(ctx, 0) }); faulted {
				want.WriteString("indefinite-error: ")
				want.WriteString(f.String())
				want.WriteString("\n")
			}
			in.close()
		}
		if want.Len() == 0 || log.String() != want.String() {
			t.Fatalf("log:\n%s\nwant:\n%s", log, want.String())
		}
	})
}

func TestAnEscapedFaultSaysWhatItIs(t *testing.T) {
	a := &abort{fault{seed: 13, site: "app.get", n: 4, phase: after}}
	want := "indefinite-error: after app.get#4 (seed=13): a fault escaped its request; only indefinite.Handler may recover it"
	if a.String() != want {
		t.Fatalf("got %q", a.String())
	}
}

// --- Replay: same seed, same faults; sites don't interfere ---

func TestSameSeedSameOutcomes(t *testing.T) {
	eachFlavor(t, func(t *testing.T, fl flavor) {
		for seed := range int64(numSeeds) {
			_, a := fl.recorder("op")
			_, b := fl.recorder("op")
			if ra, rb := run(t, seed, a, calls), run(t, seed, b, calls); !slices.Equal(ra, rb) {
				t.Fatalf("seed %d: %v != %v", seed, ra, rb)
			}
		}
	})
}

func TestARequestDependsOnItsSeedAlone(t *testing.T) {
	// seed=41 takes the same path in its first request or after 100 others.
	eachFlavor(t, func(t *testing.T, fl flavor) {
		_, o := fl.recorder("op")
		first := run(t, 41, o, calls)
		for other := range int64(100) {
			run(t, 1000+other, o, calls)
		}
		if again := run(t, 41, o, calls); !slices.Equal(again, first) {
			t.Fatalf("%v != %v", again, first)
		}
	})
}

func TestSitesAreIndependent(t *testing.T) {
	// Calls to other sites don't shift a site's decisions.
	eachFlavor(t, func(t *testing.T, fl flavor) {
		_, o := fl.recorder("op")
		_, other := fl.recorder("other")
		for seed := range int64(numSeeds) {
			alone := run(t, seed, o, calls)
			ctx, in := inject(context.Background(), seed, io.Discard)
			var mixed []string
			for i := range calls {
				attempt(t, ctx, other, i)
				mixed = append(mixed, attempt(t, ctx, o, i))
			}
			in.close()
			if !slices.Equal(mixed, alone) {
				t.Fatalf("seed %d: %v != %v", seed, mixed, alone)
			}
		}
	})
}

func TestSeedsVaryWhichPhasesASiteGets(t *testing.T) {
	// Swarm testing: per seed, a site faults never, before-only, after-only, or both.
	eachFlavor(t, func(t *testing.T, fl flavor) {
		_, o := fl.recorder("op")
		kinds := map[string]bool{}
		for seed := range int64(numSeeds) {
			phases := map[string]bool{}
			for _, outcome := range run(t, seed, o, calls) {
				if outcome != "ok" {
					phases[outcome] = true
				}
			}
			kinds[strings.Join(slices.Sorted(maps.Keys(phases)), "+")] = true
		}
		if want := []string{"", "after", "after+before", "before"}; !slices.Equal(slices.Sorted(maps.Keys(kinds)), want) {
			t.Fatalf("kinds %v, want %v", kinds, want)
		}
	})
}

// --- What the fault says ---

func TestTheFaultNamesTheSiteCallAndSeed(t *testing.T) {
	eachFlavor(t, func(t *testing.T, fl flavor) {
		_, o := fl.recorder("db.commit")
		f := firstFault(t, func(ctx context.Context) { _, _ = o(ctx, 0) }, pass)
		if f.site != "db.commit" || f.n != 0 {
			t.Fatalf("fault %+v", f)
		}
		if want := f.phase.String() + " db.commit#0 (seed=" + itoa(f.seed) + ")"; f.String() != want {
			t.Fatalf("%q, want %q", f.String(), want)
		}
	})
}

func TestCallsCountEveryCallFaultedOrNot(t *testing.T) {
	eachFlavor(t, func(t *testing.T, fl flavor) {
		_, o := fl.recorder("counted")
		for seed := range int64(numSeeds) {
			ctx, in := inject(context.Background(), seed, io.Discard)
			for i := range calls {
				attempt(t, ctx, o, i)
			}
			in.close()
			if got := in.snapshot(); !maps.Equal(got, map[string]int{"counted": calls}) {
				t.Fatalf("seed %d: calls %v", seed, got)
			}
		}
	})
}

func TestSitesWithOneNameShareOneStream(t *testing.T) {
	// A site is its name: two Sites named alike share one call counter.
	a, b, c := NewSite("shared"), NewSite("shared"), NewSite("own")
	ctx, in := request(t, 0)
	for _, s := range []Site{a, b, c} {
		_, _ = catch(func() { _ = s.Do(ctx, func() error { return nil }) })
	}
	if got := in.snapshot(); !maps.Equal(got, map[string]int{"shared": 2, "own": 1}) {
		t.Fatalf("calls %v", got)
	}
}

// --- Misuse is a bug: it panics at the call ---

func TestNewSiteRejectsNamesThatWouldBreakTheFaultLine(t *testing.T) {
	for _, name := range []string{"", "a b", "a\tb", "a\nb", "a\x00b", "a\u00a0b", "a\u200bb", "\xff"} {
		mustPanic(t, "site name must", func() { NewSite(name) })
	}
	for _, name := range []string{"db.commit", "ünïcode.sïte", "POST:/deposits#key", "中"} {
		if got := NewSite(name).String(); got != name {
			t.Errorf("NewSite(%q).String() = %q", name, got)
		}
	}
}

func TestMisuseIsABugEvenOutsideARequest(t *testing.T) {
	ctx := context.Background()
	mustPanic(t, "zero Site", func() { _ = Site{}.Do(ctx, func() error { return nil }) })
	mustPanic(t, "zero Site", func() { _, _ = Call(ctx, Site{}, func() (int, error) { return 0, nil }) })
	mustPanic(t, "nil fn for site op", func() { _ = NewSite("op").Do(ctx, nil) })
	mustPanic(t, "nil fn for site op", func() { _, _ = Call[int](ctx, NewSite("op"), nil) })
}
