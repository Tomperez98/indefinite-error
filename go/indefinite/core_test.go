package indefinite

import (
	"context"
	"fmt"
	"io"
	"maps"
	"math"
	"math/rand/v2"
	"slices"
	"strconv"
	"strings"
	"testing"
)

// The pure core: every decision is a function of (seed, site, n).

// --- The schedule: saved seeds replay the same faults, in any language ---

var symbol = map[phase]byte{pass: '.', before: 'b', after: 'a'}

func TestScheduleMatchesTheSpec(t *testing.T) {
	// A change here breaks every seed users saved from a failing run, in every language.
	for _, r := range specRows(t, "schedule.tsv") {
		if len(r) != 5 {
			t.Fatalf("malformed schedule row %q", r)
		}
		seed, err := strconv.ParseInt(r[0], 10, 64)
		if err != nil {
			t.Fatalf("schedule seed %q: %v", r[0], err)
		}
		site := specJSON[string](t, r[1])
		rate, m := rateOf(seed), modeOf(seed, site)
		calls := make([]byte, len(r[4]))
		for n := range calls {
			calls[n] = symbol[decide(seed, rate, m, site, n)]
		}
		got := []string{r[0], r[1], m.String(), strconv.FormatFloat(rate, 'g', -1, 64), string(calls)}
		if !slices.Equal(got, r) {
			t.Errorf("seed %d, site %q drifted:\n got %q\nwant %q", seed, site, got, r)
		}
	}
}

// --- Fault lines: spec/faults.tsv ---

func TestFaultLinesMatchTheSpec(t *testing.T) {
	// Call n of site faults with the spec's payload, and writes it on the line.
	for _, r := range specRows(t, "faults.tsv") {
		if len(r) != 4 {
			t.Fatalf("malformed faults row %q", r)
		}
		seed, err := strconv.ParseInt(r[0], 10, 64)
		if err != nil {
			t.Fatal(err)
		}
		n, err := strconv.Atoi(r[2])
		if err != nil {
			t.Fatal(err)
		}
		s, payload := NewSite(specJSON[string](t, r[1])), r[3]
		log := &syncBuffer{}
		ctx, in := inject(context.Background(), seed, log)
		var last fault
		var faulted bool
		for range n + 1 {
			last, faulted = catch(func() { _ = s.Do(ctx, func() error { return nil }) })
		}
		in.close()
		if !faulted || last.n != n || last.String() != payload {
			t.Errorf("call %d of %s at seed %d: faulted=%v %q, want %q", n, s, seed, faulted, last, payload)
		}
		if lines := strings.Split(strings.TrimSuffix(log.String(), "\n"), "\n"); lines[len(lines)-1] != "indefinite-error: "+payload {
			t.Errorf("last line %q, want the payload %q", lines[len(lines)-1], payload)
		}
	}
}

func TestPartsCannotRunTogether(t *testing.T) {
	// Parts are separated before hashing, so ("1", "2x") differs from ("12", "x").
	if unit("mode", "1", "2x") == unit("mode", "12", "x") {
		t.Error(`unit("mode", "1", "2x") == unit("mode", "12", "x")`)
	}
	if unit("call", "0", "a", "11") == unit("call", "0", "a1", "1") {
		t.Error(`unit("call", "0", "a", "11") == unit("call", "0", "a1", "1")`)
	}
}

// --- Decision table: every row, and exactly one row per input ---

type row struct {
	mode mode
	draw bool   // did the call's draw land under the rate?
	want string // a phase, or "coin": before if the phase draw < 0.5, else after
}

var rows = []row{
	{modeOff, false, "pass"},
	{modeOff, true, "pass"},
	{modeBefore, false, "pass"},
	{modeBefore, true, "before"},
	{modeAfter, false, "pass"},
	{modeAfter, true, "after"},
	{modeBoth, false, "pass"},
	{modeBoth, true, "coin"},
}

func TestDecisionTable(t *testing.T) {
	for _, r := range rows {
		t.Run(fmt.Sprintf("%v/draw=%v", r.mode, r.draw), func(t *testing.T) {
			rate := 0.0 // no draw is < 0.0; every draw is < 1.0
			if r.draw {
				rate = 1.0
			}
			for seed := range int64(5) {
				for _, site := range []string{"a", "b"} {
					for n := range 100 {
						want := r.want
						if want == "coin" {
							want = "after"
							if unit("phase", itoa(seed), site, itoa(int64(n))) < 0.5 {
								want = "before"
							}
						}
						if got := decide(seed, rate, r.mode, site, n); got.String() != want {
							t.Fatalf("decide(%d, %v, %v, %q, %d) = %v, want %s", seed, rate, r.mode, site, n, got, want)
						}
					}
				}
			}
		})
	}
}

func TestDecisionTableIsCompleteAndUnambiguous(t *testing.T) {
	for _, m := range modes {
		for _, draw := range []bool{false, true} {
			var matches int
			for _, r := range rows {
				if r.mode == m && r.draw == draw {
					matches++
				}
			}
			if matches != 1 {
				t.Errorf("gap or overlap at mode=%v draw=%v", m, draw)
			}
		}
	}
}

func TestDrawUnderRateIsWhatFaults(t *testing.T) {
	for seed := range int64(5) {
		for n := range 200 {
			faulted := decide(seed, 0.3, modeBefore, "site", n) != pass
			if under := unit("call", itoa(seed), "site", itoa(int64(n))) < 0.3; faulted != under {
				t.Fatalf("seed=%d n=%d: faulted=%v, draw under rate=%v", seed, n, faulted, under)
			}
		}
	}
}

func TestADrawEqualToTheRateDoesNotFault(t *testing.T) {
	// The fault window is [0, rate): rate 0.0 never faults, 1.0 always does.
	draw := unit("call", "0", "site", "0")
	if got := decide(0, draw, modeBefore, "site", 0); got != pass {
		t.Errorf("at rate == draw: %v, want pass", got)
	}
	if got := decide(0, math.Nextafter(draw, 1), modeBefore, "site", 0); got != before {
		t.Errorf("at rate just past draw: %v, want before", got)
	}
}

func TestPickCoversTheWholeUnitInterval(t *testing.T) {
	options := []string{"a", "b", "c", "d"}
	if got := pick(options, 0); got != "a" {
		t.Errorf("pick(0) = %s", got)
	}
	if got := pick(options, math.Nextafter(1, 0)); got != "d" {
		t.Errorf("pick(1-ulp) = %s", got)
	}
	for k := range 4 {
		if got := pick(options, float64(k)/4); got != options[k] {
			t.Errorf("pick(%d/4) = %s", k, got)
		}
	}
}

func TestPickPanicsOutsideTheUnitInterval(t *testing.T) {
	// A draw outside [0, 1) is a broken hash, not a choice: crash at the line.
	for _, u := range []float64{1, -0.1, math.NaN()} {
		mustPanic(t, "out of [0, 1)", func() { pick([]string{"a", "b"}, u) })
	}
}

func TestStringsOfOutOfRangeEnumsPanic(t *testing.T) {
	mustPanic(t, "phase 9 out of range", func() { _ = phase(9).String() })
	mustPanic(t, "mode 9 out of range", func() { _ = mode(9).String() })
	mustPanic(t, "mode 9 out of range", func() { decide(0, 1, mode(9), "site", 0) })
}

// --- Statistics: the seed's choices have the shape the docs promise ---
// Deterministic (the "randomness" is a hash), so these can't flake: they pass
// or fail the same way every run. Bounds are 5 standard deviations.

func within(observed, total int, p float64) bool {
	sigma := math.Sqrt(float64(total) * p * (1 - p))
	return math.Abs(float64(observed)-float64(total)*p) <= 5*sigma
}

func seedWithRate(t *testing.T, rate float64) int64 {
	t.Helper()
	for seed := range int64(10_000) {
		if rateOf(seed) == rate {
			return seed
		}
	}
	t.Fatalf("no seed has rate %v", rate)
	return 0
}

func TestObservedFaultRateMatchesTheRate(t *testing.T) {
	for _, rate := range rates {
		seed, total, faults := seedWithRate(t, rate), 20_000, 0
		for n := range total {
			if decide(seed, rate, modeBefore, "site", n) != pass {
				faults++
			}
		}
		if !within(faults, total, rate) {
			t.Errorf("%d/%d faults at rate %v", faults, total, rate)
		}
	}
}

func TestBothModeSplitsEvenly(t *testing.T) {
	seed, counts := seedWithRate(t, 0.5), map[phase]int{}
	for n := range 40_000 {
		counts[decide(seed, 0.5, modeBoth, "site", n)]++
	}
	if !within(counts[before], counts[before]+counts[after], 0.5) {
		t.Errorf("before/after split %v", counts)
	}
}

func TestModesAndRatesAreSpreadEvenly(t *testing.T) {
	// Swarm testing needs every mode and every rate, about equally often.
	modeCounts := map[mode]int{}
	for seed := range int64(1000) {
		for k := range 4 {
			modeCounts[modeOf(seed, fmt.Sprintf("site%d", k))]++
		}
	}
	assertEven(t, modeCounts, modes[:])
	rateCounts := map[float64]int{}
	for seed := range int64(4000) {
		rateCounts[rateOf(seed)]++
	}
	assertEven(t, rateCounts, rates[:])
}

func assertEven[T comparable](t *testing.T, counts map[T]int, options []T) {
	t.Helper()
	var total int
	for _, c := range counts {
		total += c
	}
	for _, o := range options {
		if !within(counts[o], total, 1/float64(len(options))) {
			t.Errorf("uneven: %v", counts)
		}
	}
}

// --- Properties: a fixed stream of inputs in `go test`; fresh ones under -fuzz ---

func TestUnitIsInTheUnitInterval(t *testing.T) {
	r := rand.New(rand.NewPCG(1, 2))
	for range 10_000 {
		if u := unit(itoa(r.Int64()), randomSite(r)); !(0 <= u && u < 1) {
			t.Fatalf("unit = %v", u)
		}
	}
}

func FuzzUnitIsInTheUnitInterval(f *testing.F) {
	f.Add("", "")
	f.Add("call", "a\x00b")
	f.Fuzz(func(t *testing.T, a, b string) {
		if u := unit(a, b); !(0 <= u && u < 1) {
			t.Fatalf("unit(%q, %q) = %v", a, b, u)
		}
	})
}

func FuzzDecideOnlyReturnsPhasesTheModeAllows(f *testing.F) {
	allowed := map[mode][]phase{
		modeOff:    {pass},
		modeBefore: {pass, before},
		modeAfter:  {pass, after},
		modeBoth:   {pass, before, after},
	}
	f.Add(int64(0), "site", uint16(0), 0.5, uint8(3))
	f.Add(int64(-1), "ünïcode", uint16(63), 1.0, uint8(2))
	f.Fuzz(func(t *testing.T, seed int64, site string, n uint16, rate float64, m uint8) {
		md := modes[int(m)%len(modes)]
		if got := decide(seed, rate, md, site, int(n)); !slices.Contains(allowed[md], got) {
			t.Fatalf("mode %v decided %v", md, got)
		}
	})
}

// Any interleaving of calls across sites: the faults are exactly the model's.
// The model keeps one counter per site and asks the pure core, so a site's
// faults can't depend on its neighbours, or on anything but the seed.
func TestInjectionMatchesAReferenceModel(t *testing.T) {
	r := rand.New(rand.NewPCG(3, 4))
	for range 200 {
		sites := make([]string, 1+r.IntN(4))
		for i := range sites {
			sites[i] = fmt.Sprintf("%s.%d", randomSite(r), i) // unique
		}
		program := make([]byte, r.IntN(60))
		for i := range program {
			program[i] = byte(r.IntN(len(sites)))
		}
		checkAgainstModel(t, r.Int64(), sites, program)
	}
}

func FuzzInjectionMatchesAReferenceModel(f *testing.F) {
	f.Add(int64(3), "app.get", "app.commit", []byte{0, 1, 1, 0, 1})
	f.Fuzz(func(t *testing.T, seed int64, a, b string, program []byte) {
		if checkSite(a) != nil || checkSite(b) != nil || a == b {
			t.Skip()
		}
		for i := range program {
			program[i] %= 2
		}
		checkAgainstModel(t, seed, []string{a, b}, program)
	})
}

func checkAgainstModel(t *testing.T, seed int64, sites []string, program []byte) {
	t.Helper()
	ops := make([]Site, len(sites))
	for i, s := range sites {
		ops[i] = NewSite(s)
	}
	ctx, in := inject(context.Background(), seed, io.Discard)
	defer in.close()
	var seen []string
	for _, k := range program {
		f, faulted := catch(func() { _ = ops[k].Do(ctx, func() error { return nil }) })
		seen = append(seen, render(f, faulted))
	}

	counts := map[string]int{}
	var want []string
	for _, k := range program {
		site := sites[k]
		n := counts[site]
		counts[site]++
		p := decide(seed, rateOf(seed), modeOf(seed, site), site, n)
		want = append(want, render(fault{seed, site, n, p}, p != pass))
	}
	if !slices.Equal(seen, want) {
		t.Fatalf("seed %d, sites %q, program %v:\n got %v\nwant %v", seed, sites, program, seen, want)
	}
	if got := in.snapshot(); !maps.Equal(got, counts) {
		t.Fatalf("calls = %v, want %v", got, counts)
	}
}

func render(f fault, faulted bool) string {
	if !faulted {
		return "-"
	}
	return f.String()
}

// randomSite is a valid site name of 1 to 8 runes, some of them non-ASCII.
func randomSite(r *rand.Rand) string {
	alphabet := []rune("abcxyz.:/#-_09üïéλ中")
	runes := make([]rune, 1+r.IntN(8))
	for i := range runes {
		runes[i] = alphabet[r.IntN(len(alphabet))]
	}
	return string(runes)
}

func mustPanic(t *testing.T, substr string, fn func()) {
	t.Helper()
	defer func() {
		t.Helper()
		v := recover()
		if v == nil {
			t.Fatalf("did not panic; want a panic containing %q", substr)
		}
		if msg := fmt.Sprint(v); !strings.Contains(msg, substr) {
			t.Fatalf("panic %q does not contain %q", msg, substr)
		}
	}()
	fn()
}
