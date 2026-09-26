// Package indefinite injects reproducible indefinite errors into the requests
// of an HTTP server: it ends a request right before or right after a call at
// your system's boundary, as if the request or its response were lost.
//
//	var commit = indefinite.NewSite("db.commit")
//
//	func save(ctx context.Context, tx *sql.Tx) error {
//		return commit.Do(ctx, tx.Commit)
//	}
//
//	handler = indefinite.Handler(handler) // tests only
//
// Outside a request carrying an X-Indefinite-Seed header, Do and Call just run
// the function. Inside one, a call may fault BEFORE the function runs (it
// never happened) or AFTER it returns (it happened; nobody was told). A call
// that isn't faulted returns what the function returned. A panic from the
// function outranks the fault and propagates unchanged.
//
// A fault writes one line to stderr, then panics with a value only Handler
// recovers, and Handler answers 500 for it. Deferred calls run, as they would
// for any panic; the server and its other requests carry on. The panic is not
// an error, so no error check or retry loop can see it.
//
// Every decision is derived from the request's seed alone: the same seed
// replays the same faults, in any process, in Go or in the Python package.
package indefinite

import (
	"context"
	"fmt"
	"unicode"
	"unicode/utf8"
)

// A Site names an operation whose outcome can be indefinite: a database
// commit, a call to another service, a message you publish. Its name goes on
// the fault line, and each site draws its own faults, so calls to one site
// never shift another's.
//
// Declare sites once, at package level:
//
//	var commit = indefinite.NewSite("db.commit")
//
// The zero Site is not usable.
type Site struct{ name string }

// NewSite returns the site called name. It panics if name is empty, or isn't
// printable UTF-8 without whitespace -- it goes on the fault line, which must
// stay unambiguous to parse.
func NewSite(name string) Site {
	if err := checkSite(name); err != nil {
		panic(err)
	}
	return Site{name: name}
}

func checkSite(name string) error {
	if name == "" || !utf8.ValidString(name) {
		return fmt.Errorf("indefinite: site name must be non-empty UTF-8, got %q", name)
	}
	for _, r := range name {
		if !unicode.IsPrint(r) || unicode.IsSpace(r) {
			return fmt.Errorf("indefinite: site name must be printable with no whitespace, got %q", name)
		}
	}
	return nil
}

// String returns the site's name.
func (s Site) String() string { return s.name }

// Do calls fn, unless the request that ctx belongs to faults this call.
// It returns fn's error, or it doesn't return at all:
//
//   - before: fn never runs, and the request ends.
//   - after: fn runs, its error is discarded, and the request ends.
//
// Pass the request's context, or one derived from it: a call that can't see
// the request's injection is never faulted.
func (s Site) Do(ctx context.Context, fn func() error) error {
	if fn == nil {
		panic("indefinite: nil fn for site " + s.name)
	}
	_, err := Call(ctx, s, func() (struct{}, error) { return struct{}{}, fn() })
	return err
}

// Call is Do for a function that returns a value.
func Call[T any](ctx context.Context, s Site, fn func() (T, error)) (T, error) {
	if s.name == "" {
		panic("indefinite: the zero Site; create one with NewSite")
	}
	if fn == nil {
		panic("indefinite: nil fn for site " + s.name)
	}
	in := injectionFrom(ctx)
	if in == nil {
		return fn()
	}
	f, faulted := in.next(s.name)
	if !faulted {
		return fn()
	}
	if f.phase == before {
		panic(in.abort(f))
	}
	_, _ = fn() // it ran: its outcome is what gets lost. If it panics, that wins.
	panic(in.abort(f))
}
