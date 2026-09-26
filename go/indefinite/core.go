package indefinite

import (
	"encoding/binary"
	"fmt"
	"strconv"
	"strings"

	"golang.org/x/crypto/blake2b"
)

// The pure core: every decision is a function of (seed, site, n). It hashes
// exactly as the Python package does, so a seed replays the same faults in a
// Go service and a Python one. testdata/schedule.txt pins it.

// phase is when a faulted call ends its request. The zero value is no fault.
type phase uint8

const (
	pass   phase = iota // no fault: the call runs and returns
	before              // the call never runs
	after               // the call runs; nobody is told
)

func (p phase) String() string {
	switch p {
	case before:
		return "before"
	case after:
		return "after"
	case pass:
		return "pass"
	}
	panic(fmt.Sprintf("indefinite: phase %d out of range", uint8(p)))
}

// mode is which phases may fault at one site, for one seed.
type mode uint8

const (
	modeOff mode = iota
	modeBefore
	modeAfter
	modeBoth
)

func (m mode) String() string {
	switch m {
	case modeOff:
		return "off"
	case modeBefore:
		return "before"
	case modeAfter:
		return "after"
	case modeBoth:
		return "both"
	}
	panic(fmt.Sprintf("indefinite: mode %d out of range", uint8(m)))
}

// Per-seed fault rates (swarm testing): some seeds are gentle, some brutal.
var (
	rates = [...]float64{0.01, 0.05, 0.2, 0.5}
	modes = [...]mode{modeOff, modeBefore, modeAfter, modeBoth}
)

// unit is a uniform float in [0, 1) from parts, stable across processes and
// languages: BLAKE2b with an 8-byte digest over the parts joined by NUL, read
// big-endian, divided by 2^64.
func unit(parts ...string) float64 {
	h, err := blake2b.New(8, nil)
	if err != nil {
		panic(fmt.Sprintf("indefinite: blake2b.New(8, nil): %v", err)) // only for size > 64 or a long key
	}
	h.Write([]byte(strings.Join(parts, "\x00")))
	return float64(binary.BigEndian.Uint64(h.Sum(nil))) / (1 << 64)
}

func pick[T any](options []T, u float64) T {
	if !(0 <= u && u < 1) {
		panic(fmt.Sprintf("indefinite: u=%v out of [0, 1)", u))
	}
	return options[int(u*float64(len(options)))]
}

func itoa(n int64) string { return strconv.FormatInt(n, 10) }

func rateOf(seed int64) float64 { return pick(rates[:], unit("rate", itoa(seed))) }

func modeOf(seed int64, site string) mode {
	return pick(modes[:], unit("mode", itoa(seed), site))
}

// decide is the phase of the n-th call to site; rate and m derive from seed.
func decide(seed int64, rate float64, m mode, site string, n int) phase {
	if m == modeOff || unit("call", itoa(seed), site, itoa(int64(n))) >= rate {
		return pass
	}
	switch m {
	case modeBefore:
		return before
	case modeAfter:
		return after
	case modeBoth:
		if unit("phase", itoa(seed), site, itoa(int64(n))) < 0.5 {
			return before
		}
		return after
	}
	panic(fmt.Sprintf("indefinite: mode %d out of range", uint8(m)))
}
