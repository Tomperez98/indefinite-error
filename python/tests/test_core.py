"""The pure core: every decision is a function of (seed, site, n).

Regenerate the golden schedule -- only when you mean to break every saved seed:

    uv run python -m tests.test_core --regen
"""

from __future__ import annotations

import math
import os
import subprocess
import sys
from collections import Counter
from itertools import product
from pathlib import Path
from typing import TYPE_CHECKING, Literal

import pytest
from hypothesis import given, strategies as st

from indefinite_error import (
    _MODES,
    _RATES,
    Fault,
    Mode,
    Phase,
    _Abort,
    _decide,
    _inject,
    _mode,
    _pick,
    _rate,
    _unit,
    indefinite,
)

if TYPE_CHECKING:
    from collections.abc import Callable

# --- Golden schedule: saved seeds keep replaying the same faults ---------------

GOLDEN = Path(__file__).parent / "golden" / "schedule.txt"
GOLDEN_SEEDS = [*range(16), 41, -1, 2**64, 2**200]
GOLDEN_SITES = ["app.commit", "app.get", "ünïcode.sïte", "a\x00b"]
GOLDEN_CALLS = 64
_SYMBOL: dict[Phase | None, str] = {None: ".", "before": "b", "after": "a"}


def render_schedule() -> str:
    lines = [
        "# One line per (seed, site): mode, rate, then one char per call n=0..63.",
        "# '.' no fault, 'b' before, 'a' after. Regenerate: uv run python -m tests.test_core --regen",
    ]
    for seed in GOLDEN_SEEDS:
        rate = _rate(seed)
        for site in GOLDEN_SITES:
            mode = _mode(seed, site)
            calls = "".join(
                _SYMBOL[_decide(seed, rate, mode, site, n)] for n in range(GOLDEN_CALLS)
            )
            lines.append(f"{seed} {site!r} {mode} {rate} {calls}")
    return "\n".join(lines) + "\n"


def test_schedule_matches_golden() -> None:
    """A change here breaks every seed users saved from a failing run."""
    assert render_schedule() == GOLDEN.read_text(encoding="utf-8")


@pytest.mark.parametrize("hash_seed", ["0", "1", "random"])
def test_schedule_is_the_same_in_any_process(hash_seed: str) -> None:
    """``hash()`` is salted per process; the schedule must not be."""
    root = Path(__file__).parent.parent
    code = "from tests.test_core import render_schedule; print(render_schedule(), end='')"
    env = {**os.environ, "PYTHONHASHSEED": hash_seed, "PYTHONIOENCODING": "utf-8"}
    out = subprocess.run(
        [sys.executable, "-c", code], cwd=root, env=env, capture_output=True, check=True
    )
    assert out.stdout.decode() == GOLDEN.read_text(encoding="utf-8")


def test_parts_cannot_run_together() -> None:
    """Parts are separated before hashing, so ("1", "2x") differs from ("12", "x")."""
    assert _unit("mode", 1, "2x") != _unit("mode", 12, "x")
    assert _unit("call", 0, "a", 11) != _unit("call", 0, "a1", 1)


# --- Decision table: every row, and exactly one row per input -------------------

type Draw = bool  # did the call's draw land under the rate?
type Expected = Phase | Literal["coin"] | None

ROWS: list[tuple[Mode, Draw, Expected]] = [
    ("off", False, None),
    ("off", True, None),
    ("before", False, None),
    ("before", True, "before"),
    ("after", False, None),
    ("after", True, "after"),
    ("both", False, None),
    ("both", True, "coin"),  # before if the phase draw < 0.5, else after
]


@pytest.mark.parametrize(("mode", "draw", "expected"), ROWS)
def test_decision_table_row(mode: Mode, draw: Draw, expected: Expected) -> None:
    rate = 1.0 if draw else 0.0  # every draw is < 1.0; none is < 0.0
    for seed, site, n in product(range(5), ["a", "b"], range(100)):
        coin: Phase = "before" if _unit("phase", seed, site, n) < 0.5 else "after"
        want = coin if expected == "coin" else expected
        assert _decide(seed, rate, mode, site, n) == want, (seed, site, n)


def test_decision_table_is_complete_and_unambiguous() -> None:
    for mode, draw in product(_MODES, [False, True]):
        matches = [r for r in ROWS if r[:2] == (mode, draw)]
        assert len(matches) == 1, f"gap or overlap at mode={mode} draw={draw}"


def test_draw_under_rate_is_what_faults() -> None:
    for seed, n in product(range(5), range(200)):
        faulted = _decide(seed, 0.3, "before", "site", n) is not None
        assert faulted == (_unit("call", seed, "site", n) < 0.3)


def test_a_draw_equal_to_the_rate_does_not_fault() -> None:
    """The fault window is [0, rate): rate 0.0 never faults, 1.0 always does."""
    draw = _unit("call", 0, "site", 0)
    assert _decide(0, draw, "before", "site", 0) is None
    assert _decide(0, math.nextafter(draw, 1), "before", "site", 0) == "before"


def test_pick_covers_the_whole_unit_interval() -> None:
    options = ("a", "b", "c", "d")
    assert _pick(options, 0.0) == "a"
    assert _pick(options, math.nextafter(1.0, 0.0)) == "d"
    assert [_pick(options, k / 4) for k in range(4)] == list(options)


@pytest.mark.parametrize("u", [1.0, -0.1])
def test_pick_panics_outside_the_unit_interval(u: float) -> None:
    """A draw outside [0, 1) is a broken hash, not a choice: crash at the line."""
    with pytest.raises(AssertionError, match=r"out of \[0, 1\)"):
        _pick(("a", "b"), u)


# --- Statistics: the seed's choices have the shape the docs promise ---------------
# Deterministic (the "randomness" is a hash), so these can't flake: they pass or
# fail the same way every run. Bounds are 5 standard deviations.


def _within(observed: int, total: int, p: float) -> bool:
    sigma = math.sqrt(total * p * (1 - p))
    return abs(observed - total * p) <= 5 * sigma


def _seed_with_rate(rate: float) -> int:
    return next(s for s in range(10_000) if _rate(s) == rate)


@pytest.mark.parametrize("rate", _RATES)
def test_observed_fault_rate_matches_the_reported_rate(rate: float) -> None:
    seed, total = _seed_with_rate(rate), 20_000
    faults = sum(_decide(seed, rate, "before", "site", n) is not None for n in range(total))
    assert _within(faults, total, rate), f"{faults}/{total} faults at rate {rate}"


def test_both_mode_splits_evenly() -> None:
    seed, total = _seed_with_rate(0.5), 40_000
    phases = Counter(_decide(seed, 0.5, "both", "site", n) for n in range(total))
    faults = phases["before"] + phases["after"]
    assert _within(phases["before"], faults, 0.5), phases


def test_modes_and_rates_are_spread_evenly() -> None:
    """Swarm testing needs every mode and every rate, about equally often."""
    _assert_even(Counter(_mode(seed, f"site{k}") for seed in range(1000) for k in range(4)), _MODES)
    _assert_even(Counter(_rate(seed) for seed in range(4000)), _RATES)


def _assert_even[T](counts: Counter[T], options: tuple[T, ...]) -> None:
    total = sum(counts.values())
    for option in options:
        assert _within(counts[option], total, 1 / len(options)), counts


# --- Properties -----------------------------------------------------------------

parts = st.lists(st.one_of(st.integers(), st.text()), max_size=5)


@given(parts)
def test_unit_is_in_the_unit_interval(values: list[int | str]) -> None:
    assert 0.0 <= _unit(*values) < 1.0


@given(st.integers(), st.text(), st.integers(min_value=0), st.floats(0, 1), st.sampled_from(_MODES))
def test_decide_only_returns_phases_the_mode_allows(
    seed: int, site: str, n: int, rate: float, mode: Mode
) -> None:
    allowed: dict[Mode, set[Phase | None]] = {
        "off": {None},
        "before": {None, "before"},
        "after": {None, "after"},
        "both": {None, "before", "after"},
    }
    assert _decide(seed, rate, mode, site, n) in allowed[mode]


@given(
    seed=st.integers(),
    sites=st.lists(
        st.text(min_size=1).filter(lambda s: s.isprintable() and not any(c.isspace() for c in s)),
        min_size=1,
        max_size=4,
        unique=True,
    ),
    data=st.data(),
)
def test_injection_matches_a_reference_model(
    seed: int, sites: list[str], data: st.DataObject
) -> None:
    """Any interleaving of calls across sites: faults are exactly the model's.

    The model keeps one counter per site and asks the pure core; so a site's
    faults can't depend on its neighbours, or on anything but the seed.
    """
    program = data.draw(st.lists(st.sampled_from(sites), max_size=60))
    ops: dict[str, Callable[[], None]] = {s: indefinite(name=s)(lambda: None) for s in sites}

    seen: list[Fault | None] = []
    with _inject(seed) as inj:
        for site in program:
            try:
                ops[site]()
                seen.append(None)
            except _Abort as e:
                seen.append(e.fault)

    counts: Counter[str] = Counter()
    expected: list[Fault | None] = []
    for site in program:
        n = counts[site]
        counts[site] += 1
        phase = _decide(seed, _rate(seed), _mode(seed, site), site, n)
        expected.append(None if phase is None else Fault(seed, site, n, phase))

    assert seen == expected
    assert inj.calls == dict(counts)


if __name__ == "__main__":
    if sys.argv[1:] != ["--regen"]:
        sys.exit("usage: python -m tests.test_core --regen")
    GOLDEN.parent.mkdir(exist_ok=True)
    GOLDEN.write_text(render_schedule(), encoding="utf-8")
    print(f"wrote {GOLDEN}")  # noqa: T201
