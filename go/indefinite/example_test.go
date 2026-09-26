package indefinite_test

import (
	"context"
	"database/sql"
	"flag"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"

	"github.com/Tomperez98/indefinite-error/go/indefinite"
)

// The same add(1) under three seeds: one loses the write, one loses the
// response, one is clean. Seed 74 is the bug this package exists to find: the
// write committed, so a client that retries on the 500 applies it twice.
func Example() {
	var ledger []int
	add := indefinite.NewSite("ledger.add") // the boundary write that can lose its outcome

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
	// Output:
	// seed=70: status=500, ledger=[]
	// seed=74: status=500, ledger=[1]
	// seed=0: status=200, ledger=[1]
}

var commit = indefinite.NewSite("db.commit")

func ExampleSite_Do() {
	save := func(ctx context.Context, tx *sql.Tx) error {
		return commit.Do(ctx, tx.Commit)
	}
	_ = save
}

type Item struct{ ID string }

var fetch = indefinite.NewSite("inventory.get")

func ExampleCall() {
	get := func(ctx context.Context, c *http.Client, id string) (*Item, error) {
		return indefinite.Call(ctx, fetch, func() (*Item, error) {
			req, err := http.NewRequestWithContext(ctx, http.MethodGet, "http://inventory/items/"+id, nil)
			if err != nil {
				return nil, err
			}
			resp, err := c.Do(req)
			if err != nil {
				return nil, err
			}
			defer func() { _ = resp.Body.Close() }()
			return &Item{ID: id}, nil
		})
	}
	_ = get
}

func ExampleHandler() {
	faults := flag.Bool("indefinite-errors", false, "inject faults into seeded requests; never in production")
	flag.Parse()

	mux := http.NewServeMux()
	var h http.Handler = mux
	if *faults {
		h = indefinite.Handler(h) // inside any panic-recovering middleware
	}
	_ = http.ListenAndServe(":8080", h)
}
