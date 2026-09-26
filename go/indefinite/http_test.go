package indefinite

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"maps"
	"net"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// Handler: one injection per request, and the table it answers with.

// ledger is the state a writing app changes.
type ledger struct {
	mu      sync.Mutex
	entries []int
}

func (l *ledger) add(x int) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.entries = append(l.entries, x)
}

func (l *ledger) len() int {
	l.mu.Lock()
	defer l.mu.Unlock()
	return len(l.entries)
}

func (l *ledger) reset() {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.entries = nil
}

var appWrite = NewSite("app.write")

// writingApp writes 1 to its ledger, then answers 201.
func writingApp() (*ledger, http.Handler) {
	l := &ledger{}
	return l, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_ = appWrite.Do(r.Context(), func() error { l.add(1); return nil })
		w.WriteHeader(http.StatusCreated)
		_, _ = io.WriteString(w, "created")
	})
}

func seeded(seed int64) *http.Request {
	r := httptest.NewRequest(http.MethodPost, "/", nil)
	r.Header.Set(SeedHeader, itoa(seed))
	return r
}

func serve(h http.Handler, r *http.Request) *httptest.ResponseRecorder {
	w := httptest.NewRecorder()
	h.ServeHTTP(w, r)
	return w
}

// --- The table ---

func TestRowsMatchTheTable(t *testing.T) {
	// No fault: the real response; before: 500, unchanged; after: 500, changed.
	l, app := writingApp()
	h := handler(app, io.Discard)
	rows := map[string]bool{}
	for seed := range int64(numSeeds) {
		l.reset()
		w := serve(h, seeded(seed))
		f := w.Header().Get(FaultHeader)
		ph, _, _ := strings.Cut(f, " ")
		rows[fmt.Sprintf("%d %s changed=%v", w.Code, ph, l.len() == 1)] = true
		if f != "" && !strings.HasSuffix(f, fmt.Sprintf("(seed=%d)", seed)) {
			t.Fatalf("fault %q doesn't name seed %d", f, seed)
		}
	}
	want := []string{"201  changed=true", "500 after changed=true", "500 before changed=false"}
	if got := slices.Sorted(maps.Keys(rows)); !slices.Equal(got, want) {
		t.Fatalf("rows %q, want %q", got, want)
	}
}

func TestTheFaultResponseIsFresh(t *testing.T) {
	// An empty body, and none of the headers the lost request set -- but the
	// ones middleware outside Handler set survive.
	s := NewSite("app.fresh")
	inner := handler(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("X-Outer", "overwritten")
		_ = s.Do(r.Context(), func() error { return nil })
		w.WriteHeader(http.StatusOK)
	}), io.Discard)
	outer := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Outer", "kept")
		inner.ServeHTTP(w, r)
	})
	for seed := range int64(numSeeds) {
		w := serve(outer, seeded(seed))
		if w.Code != http.StatusInternalServerError {
			continue
		}
		if w.Body.Len() != 0 || w.Header().Get("Content-Type") != "" || w.Header().Get("X-Outer") != "kept" {
			t.Fatalf("fault response: body %q, headers %v", w.Body, w.Header())
		}
		return
	}
	t.Fatal("no seed faulted")
}

func TestTheSeedIsAWholeRequestInput(t *testing.T) {
	// The same seed takes the same path at request 1 or request 100.
	_, app := writingApp()
	h := handler(app, io.Discard)
	responses := func() []string {
		var out []string
		for seed := range int64(50) {
			w := serve(h, seeded(seed))
			out = append(out, fmt.Sprint(w.Code, w.Header().Get(FaultHeader)))
		}
		return out
	}
	first := responses()
	for other := range int64(100) {
		serve(h, seeded(1000+other))
	}
	if again := responses(); !slices.Equal(again, first) {
		t.Fatalf("%v != %v", again, first)
	}
}

func TestAFaultEndsOneRequestNotItsNeighbours(t *testing.T) {
	// Concurrent requests to a real server: each gets the response it gets alone.
	_, app := writingApp()
	alone := make([]string, numSeeds)
	for seed := range numSeeds {
		w := serve(handler(app, io.Discard), seeded(int64(seed)))
		alone[seed] = fmt.Sprint(w.Code, w.Header().Get(FaultHeader))
	}

	// Every request waits until two have been in the app at once, so the
	// requests provably overlap; a stuck wait means they ran one at a time.
	var inApp atomic.Int32
	overlapped := make(chan struct{})
	var once sync.Once
	srv := httptest.NewServer(handler(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if inApp.Add(1) >= 2 {
			once.Do(func() { close(overlapped) })
		}
		defer inApp.Add(-1)
		select {
		case <-overlapped:
		case <-time.After(5 * time.Second):
			t.Error("no two requests were ever in the app at once")
		}
		app.ServeHTTP(w, r)
	}), io.Discard))
	defer srv.Close()

	// Bounded: an unbounded burst overflows the listen backlog (128 on macOS),
	// and the kernel resets the connections past it.
	const inFlight = 32
	client := srv.Client()
	client.Transport.(*http.Transport).MaxIdleConnsPerHost = inFlight
	slots := make(chan struct{}, inFlight)
	got := make([]string, numSeeds)
	var wg sync.WaitGroup
	for seed := range numSeeds {
		wg.Go(func() {
			slots <- struct{}{}
			defer func() { <-slots }()
			req, _ := http.NewRequest(http.MethodPost, srv.URL, nil)
			req.Header.Set(SeedHeader, itoa(int64(seed)))
			resp, err := client.Do(req)
			if err != nil {
				got[seed] = err.Error()
				return
			}
			defer func() { _ = resp.Body.Close() }()
			got[seed] = fmt.Sprint(resp.StatusCode, resp.Header.Get(FaultHeader))
		})
	}
	wg.Wait()
	if !slices.Equal(got, alone) {
		t.Fatalf("concurrent responses differ from lone ones:\n%v\n%v", got, alone)
	}
}

func TestAFaultCarriedBackFromAGoroutineEndsTheRequest(t *testing.T) {
	// The documented pattern: a goroutine recovers and hands the panic back.
	s := NewSite("app.child")
	h := handler(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		panicked := make(chan any, 1)
		go func() {
			defer func() { panicked <- recover() }()
			_ = s.Do(r.Context(), func() error { return nil })
		}()
		if v := <-panicked; v != nil {
			panic(v)
		}
		w.WriteHeader(http.StatusOK)
	}), io.Discard)
	statuses := map[int]bool{}
	for seed := range int64(numSeeds) {
		statuses[serve(h, seeded(seed)).Code] = true
	}
	if len(statuses) != 2 || !statuses[200] || !statuses[500] {
		t.Fatalf("statuses %v", statuses)
	}
}

// --- Too late to answer ---

var appStream = NewSite("app.stream")

func TestTooLateToAnswerDropsTheConnection(t *testing.T) {
	starts := map[string]func(w http.ResponseWriter){
		"WriteHeader": func(w http.ResponseWriter) { w.WriteHeader(http.StatusOK) },
		"Write":       func(w http.ResponseWriter) { _, _ = w.Write([]byte("chunk")) },
		"Flush":       func(w http.ResponseWriter) { w.(http.Flusher).Flush() },
		"ResponseController.Flush": func(w http.ResponseWriter) {
			if err := http.NewResponseController(w).Flush(); err != nil {
				t.Fatal(err)
			}
		},
	}
	for name, start := range starts {
		t.Run(name, func(t *testing.T) {
			h := handler(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				start(w)
				_ = appStream.Do(r.Context(), func() error { return nil })
			}), io.Discard)
			var aborted int
			for seed := range int64(numSeeds) {
				func() {
					defer func() {
						switch v := recover(); v {
						case nil:
						case http.ErrAbortHandler:
							aborted++
						default:
							panic(v)
						}
					}()
					serve(h, seeded(seed))
				}()
			}
			if aborted == 0 {
				t.Fatal("no seed faulted")
			}
		})
	}
}

func TestTooLateToAnswerOverTheWire(t *testing.T) {
	// The server closes the connection mid-response; the client sees it cut.
	h := handler(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Length", "10")
		_, _ = io.WriteString(w, "chunk")
		_ = http.NewResponseController(w).Flush()
		_ = appStream.Do(r.Context(), func() error { return nil })
		_, _ = io.WriteString(w, "chunk")
	}), io.Discard)
	srv := httptest.NewServer(h)
	defer srv.Close()
	outcomes := map[string]bool{}
	for seed := range int64(numSeeds) {
		req, _ := http.NewRequest(http.MethodGet, srv.URL, nil)
		req.Header.Set(SeedHeader, itoa(seed))
		resp, err := srv.Client().Do(req)
		if err != nil {
			t.Fatal(err)
		}
		body, err := io.ReadAll(resp.Body)
		_ = resp.Body.Close()
		switch {
		case err == nil && string(body) == "chunkchunk":
			outcomes["whole"] = true
		case errors.Is(err, io.ErrUnexpectedEOF) && string(body) == "chunk":
			outcomes["cut"] = true
		default:
			t.Fatalf("seed %d: body %q, err %v", seed, body, err)
		}
	}
	if !outcomes["whole"] || !outcomes["cut"] {
		t.Fatalf("outcomes %v", outcomes)
	}
}

func TestInformationalHeadersDoNotStartTheResponse(t *testing.T) {
	s := NewSite("app.early")
	h := handler(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Link", "</style.css>; rel=preload")
		w.WriteHeader(http.StatusEarlyHints)
		_ = s.Do(r.Context(), func() error { return nil })
		w.WriteHeader(http.StatusOK)
	}), io.Discard)
	for seed := range int64(numSeeds) {
		if w := serve(h, seeded(seed)); w.Code == http.StatusInternalServerError || w.Header().Get(FaultHeader) != "" {
			return // answered for, despite the 103 before it
		}
	}
	t.Fatal("no seed answered a fault after 103 Early Hints")
}

func TestRequestFaultsWriteTheLine(t *testing.T) {
	_, app := writingApp()
	log := &syncBuffer{}
	h := handler(app, log)
	var want strings.Builder
	for seed := range int64(numSeeds) {
		if f := serve(h, seeded(seed)).Header().Get(FaultHeader); f != "" {
			want.WriteString("indefinite-error: ")
			want.WriteString(f)
			want.WriteString("\n")
		}
	}
	if want.Len() == 0 || log.String() != want.String() {
		t.Fatalf("log:\n%s\nwant:\n%s", log, want.String())
	}

}

// --- What Handler passes through untouched ---

func TestPassesThroughWithoutASeed(t *testing.T) {
	l, app := writingApp()
	h := handler(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if injectionFrom(r.Context()) != nil {
			t.Error("a request without a seed got an injection")
		}
		app.ServeHTTP(w, r)
	}), io.Discard)
	for range 50 {
		if w := serve(h, httptest.NewRequest(http.MethodPost, "/", nil)); w.Code != http.StatusCreated {
			t.Fatalf("status %d", w.Code)
		}
	}
	if l.len() != 50 {
		t.Fatalf("ledger has %d entries, want 50", l.len())
	}
}

func TestAnAppPanicIsNotOursToAnswer(t *testing.T) {
	for _, want := range []any{fatal{}, errDefinite, http.ErrAbortHandler} {
		h := handler(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { panic(want) }), io.Discard)
		func() {
			defer func() {
				if got := recover(); got != want {
					t.Fatalf("recovered %v, want %v unchanged", got, want)
				}
			}()
			serve(h, seeded(1))
		}()
	}
}

func TestTheSeedHeaderMatchesTheSpec(t *testing.T) {
	// A request runs under the spec's seed, or gets a 400 and never reaches the app.
	for _, r := range specRows(t, "seed-header.tsv") {
		if len(r) != 2 {
			t.Fatalf("malformed seed-header row %q", r)
		}
		values := specJSON[[]string](t, r[0])
		var seeds []int64
		h := handler(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			seeds = append(seeds, injectionFrom(r.Context()).seed)
		}), io.Discard)
		req := httptest.NewRequest(http.MethodPost, "/", nil)
		req.Header[SeedHeader] = values
		w := serve(h, req)
		got := fmt.Sprint(w.Code, seeds)
		want := "400 []"
		if r[1] != "400" {
			want = fmt.Sprintf("200 [%s]", r[1])
		}
		if got != want {
			t.Errorf("%s: got %s, want %s", r[0], got, want)
		}
	}
}

func TestAMalformedSeedIsA400(t *testing.T) {
	// Bytes the spec's UTF-8 can't carry get a 400 too; the body says why.
	for _, raw := range []string{"abc", "\xff", strings.Repeat("1", 65)} {
		l, app := writingApp()
		r := httptest.NewRequest(http.MethodPost, "/", nil)
		r.Header.Set(SeedHeader, raw)
		w := serve(handler(app, io.Discard), r)
		if w.Code != http.StatusBadRequest || w.Body.String() != "X-Indefinite-Seed must be a decimal int64\n" {
			t.Errorf("%q: %d %q", raw, w.Code, w.Body)
		}
		if l.len() != 0 {
			t.Errorf("%q: the app ran", raw)
		}
	}
}

func TestInstalledTwiceIsALoudMisconfiguration(t *testing.T) {
	_, app := writingApp()
	h := handler(handler(app, io.Discard), io.Discard)
	mustPanic(t, "indefinite: a request seeded 1 inside one seeded 1: is indefinite.Handler installed twice?", func() {
		serve(h, seeded(1))
	})
}

func TestTheExportedHandlerServesTheApp(t *testing.T) {
	// Handler logs to stderr; the rest of the file injects a log to read it.
	_, app := writingApp()
	if w := serve(Handler(app), httptest.NewRequest(http.MethodPost, "/", nil)); w.Code != http.StatusCreated {
		t.Fatalf("status %d", w.Code)
	}
}

// --- The ResponseWriter it hands the app keeps its features ---

type hijackable struct {
	http.ResponseWriter
	err error
}

func (h hijackable) Hijack() (net.Conn, *bufio.ReadWriter, error) { return nil, nil, h.err }

func TestHijackStartsTheResponseOnlyIfItWorks(t *testing.T) {
	for _, tc := range []struct {
		rw      http.ResponseWriter
		started bool
	}{
		{hijackable{httptest.NewRecorder(), nil}, true},
		{hijackable{httptest.NewRecorder(), http.ErrNotSupported}, false},
		{httptest.NewRecorder(), false}, // can't hijack at all
	} {
		w := &writer{ResponseWriter: tc.rw}
		_, _, err := w.Hijack()
		if w.started != tc.started || (err == nil) != tc.started {
			t.Errorf("%T: started=%v err=%v", tc.rw, w.started, err)
		}
	}
}

func TestAFailedFlushDoesNotStartTheResponse(t *testing.T) {
	type bare struct{ http.ResponseWriter } // hides the recorder's Flush
	w := &writer{ResponseWriter: bare{httptest.NewRecorder()}}
	if err := w.FlushError(); !errors.Is(err, http.ErrNotSupported) || w.started {
		t.Fatalf("err=%v started=%v", err, w.started)
	}
	if w.Unwrap() == nil {
		t.Fatal("Unwrap lost the ResponseWriter")
	}
}
