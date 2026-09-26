package indefinite

import (
	"bufio"
	"io"
	"maps"
	"net"
	"net/http"
	"os"
	"strconv"
	"strings"
)

// The headers Handler reads and writes.
const (
	// SeedHeader carries a request's seed: one decimal int64, such as "13" or "-7".
	SeedHeader = "X-Indefinite-Seed"
	// FaultHeader names the fault that ended a request: "after db.commit#0 (seed=13)".
	// It is for debugging; a test's assertions must not read it.
	FaultHeader = "X-Indefinite-Fault"
)

// Handler runs each request carrying [SeedHeader] inside its own injection,
// driven by that seed. Requests without the header pass through untouched; a
// malformed seed gets a 400, and h never sees the request.
//
// A fault unwinds its request to Handler, which answers 500 with an empty body
// and a [FaultHeader] naming the fault -- the closest an HTTP handler can come
// to losing a request or its response. If the response has already started,
// Handler panics with [http.ErrAbortHandler] instead, and the server drops the
// connection mid-response.
//
// A fault panics the goroutine that made the call. Handler recovers it on the
// goroutine that runs h; on a goroutine h started, it crashes the process,
// like any panic there, unless that goroutine carries it back to h's.
//
// Install Handler inside any middleware that recovers panics, so it recovers
// its faults before they can. Never install it in production: any caller
// could fault your server.
func Handler(h http.Handler) http.Handler { return handler(h, os.Stderr) }

func handler(h http.Handler, log io.Writer) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		raw, ok := r.Header[http.CanonicalHeaderKey(SeedHeader)]
		if !ok {
			h.ServeHTTP(w, r)
			return
		}
		seed, ok := parseSeed(raw)
		if !ok {
			http.Error(w, SeedHeader+" must be a decimal int64", http.StatusBadRequest)
			return
		}

		ctx, in := inject(r.Context(), seed, log)
		saved := w.Header().Clone() // what the middleware outside us set
		tw := &writer{ResponseWriter: w}
		defer func() {
			in.close()
			v := recover()
			if v == nil {
				return // returned normally, or runtime.Goexit: let it carry on
			}
			a, ok := v.(*abort)
			if !ok {
				panic(v) // not ours
			}
			if tw.started {
				panic(http.ErrAbortHandler) // too late to answer: drop the connection
			}
			// A fresh response: nothing h set survives the lost request.
			clear(w.Header())
			maps.Copy(w.Header(), saved)
			w.Header().Set(FaultHeader, a.fault.String())
			w.WriteHeader(http.StatusInternalServerError)
		}()
		h.ServeHTTP(tw, r.WithContext(ctx))
	})
}

// parseSeed parses exactly one header value of the form -?[0-9]{1,19} into an
// int64, as spec/seed-header.tsv pins. The header is untrusted input: "+1",
// " 1", and "1_0" are all malformed.
func parseSeed(values []string) (int64, bool) {
	if len(values) != 1 {
		return 0, false
	}
	raw := values[0]
	digits := strings.TrimPrefix(raw, "-")
	if len(digits) < 1 || len(digits) > 19 {
		return 0, false
	}
	for i := range len(digits) {
		if digits[i] < '0' || digits[i] > '9' {
			return 0, false
		}
	}
	seed, err := strconv.ParseInt(raw, 10, 64)
	return seed, err == nil // only a range error is left
}

// writer tracks whether the response has started, past which Handler can't
// answer for a fault. Unwrap lets [http.ResponseController] reach the
// ResponseWriter underneath.
type writer struct {
	http.ResponseWriter
	started bool
}

func (w *writer) WriteHeader(code int) {
	if code >= 200 || code == http.StatusSwitchingProtocols {
		w.started = true // 1xx informational headers don't start the response
	}
	w.ResponseWriter.WriteHeader(code)
}

func (w *writer) Write(b []byte) (int, error) {
	w.started = true
	return w.ResponseWriter.Write(b)
}

func (w *writer) Flush() { _ = w.FlushError() }

func (w *writer) FlushError() error {
	err := http.NewResponseController(w.ResponseWriter).Flush()
	if err == nil {
		w.started = true
	}
	return err
}

func (w *writer) Hijack() (net.Conn, *bufio.ReadWriter, error) {
	conn, rw, err := http.NewResponseController(w.ResponseWriter).Hijack()
	if err == nil {
		w.started = true
	}
	return conn, rw, err
}

func (w *writer) Unwrap() http.ResponseWriter { return w.ResponseWriter }
