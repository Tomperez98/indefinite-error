package indefinite

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
)

// Shared test helpers. They fail the test themselves; none returns an error.

const (
	numSeeds = 300 // seeds 0..299
	calls    = 50
)

type definiteError struct{}

func (definiteError) Error() string { return "a failure the operation itself reports" }

var errDefinite error = definiteError{}

// op is a site call behind one signature: what a handler would call.
type op func(ctx context.Context, i int) (int, error)

// A flavor is one entry point to the contract: Call, or Site.Do. Tests that
// range over flavors hold both to it.
type flavor struct {
	name string
	call func(ctx context.Context, s Site, fn func() (int, error)) (int, error)
}

var flavors = []flavor{
	{"Call", Call[int]},
	{"Do", func(ctx context.Context, s Site, fn func() (int, error)) (int, error) {
		var v int
		err := s.Do(ctx, func() error {
			var err error
			v, err = fn()
			return err
		})
		return v, err
	}},
}

func eachFlavor(t *testing.T, test func(t *testing.T, fl flavor)) {
	t.Helper()
	for _, fl := range flavors {
		t.Run(fl.name, func(t *testing.T) { test(t, fl) })
	}
}

// recorder is an op at site that records each real execution and returns i*2.
func (fl flavor) recorder(site string) (*[]int, op) {
	s := NewSite(site)
	var ran []int
	return &ran, func(ctx context.Context, i int) (int, error) {
		return fl.call(ctx, s, func() (int, error) {
			ran = append(ran, i)
			return i * 2, nil
		})
	}
}

// catch runs fn and returns the fault that ended it, if one did. Recovering
// the abort here stands in for Handler, its only recoverer.
func catch(fn func()) (f fault, faulted bool) {
	defer func() {
		if v := recover(); v != nil {
			a, ok := v.(*abort)
			if !ok {
				panic(v)
			}
			f, faulted = a.fault, true
		}
	}()
	fn()
	return fault{}, false
}

// attempt is "ok" or the injected phase: one call is one request.
func attempt(t *testing.T, ctx context.Context, o op, i int) string {
	t.Helper()
	f, faulted := catch(func() {
		got, err := o(ctx, i)
		if err != nil || got != i*2 {
			t.Fatalf("op(%d) = %d, %v; want %d, nil", i, got, err, i*2)
		}
	})
	if faulted {
		return f.phase.String()
	}
	return "ok"
}

// request is a context inside a fresh injection seeded seed, closed at cleanup.
func request(t *testing.T, seed int64) (context.Context, *injection) {
	t.Helper()
	ctx, in := inject(context.Background(), seed, io.Discard)
	t.Cleanup(in.close)
	return ctx, in
}

// run is the outcomes of n calls to o in one injection.
func run(t *testing.T, seed int64, o op, n int) []string {
	t.Helper()
	ctx, in := inject(context.Background(), seed, io.Discard)
	defer in.close()
	out := make([]string, n)
	for i := range n {
		out[i] = attempt(t, ctx, o, i)
	}
	return out
}

// firstFault is the first fault call hits across the seeds, of phase p unless p is pass.
func firstFault(t *testing.T, call func(ctx context.Context), p phase) fault {
	t.Helper()
	for seed := range int64(numSeeds) {
		ctx, in := inject(context.Background(), seed, io.Discard)
		f, faulted := catch(func() { call(ctx) })
		in.close()
		if faulted && (p == pass || f.phase == p) {
			return f
		}
	}
	t.Fatalf("no seed in 0..%d faulted %v", numSeeds-1, p)
	return fault{}
}

// seedWhere is the first seed that gives site mode m.
func seedWhere(t *testing.T, site string, m mode) int64 {
	t.Helper()
	for seed := range int64(numSeeds) {
		if modeOf(seed, site) == m {
			return seed
		}
	}
	t.Fatalf("no seed in 0..%d gives %s mode %v", numSeeds-1, site, m)
	return 0
}

// syncBuffer is a log that concurrent requests can share.
type syncBuffer struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (b *syncBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.buf.Write(p)
}

func (b *syncBuffer) String() string {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.buf.String()
}

// specDir is the repository's spec/: the contract every implementation is
// tested against. A module downloaded from a proxy has no spec/ beside it, so
// there the spec tests skip. Set INDEFINITE_ERROR_SPEC=required in CI, so in
// the repository a missing spec fails.
const specDir = "../../spec"

// specRows is the tab-separated rows of spec/<name>, without its # comments.
func specRows(t *testing.T, name string) [][]string {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(specDir, name))
	if errors.Is(err, fs.ErrNotExist) && os.Getenv("INDEFINITE_ERROR_SPEC") != "required" {
		t.Skipf("no %s: not in a repository checkout", filepath.Join(specDir, name))
	}
	if err != nil {
		t.Fatal(err)
	}
	var rows [][]string
	for line := range strings.Lines(string(b)) {
		line = strings.TrimSuffix(line, "\n")
		if line != "" && !strings.HasPrefix(line, "#") {
			rows = append(rows, strings.Split(line, "\t"))
		}
	}
	if len(rows) == 0 {
		t.Fatalf("spec/%s has no rows", name)
	}
	return rows
}

// specJSON decodes one JSON cell of a spec row into a T.
func specJSON[T any](t *testing.T, cell string) T {
	t.Helper()
	var v T
	if err := json.Unmarshal([]byte(cell), &v); err != nil {
		t.Fatalf("spec cell %s: %v", cell, err)
	}
	return v
}
