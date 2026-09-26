"""The contract every ``@indefinite`` operation keeps, ``def`` or ``async def``.

Each test takes ``flavor`` and runs once per wrapper. A test catching
``_Abort`` stands in for the middleware; ``test_asgi.py`` has the real one.
"""

from __future__ import annotations

import inspect
from typing import Any

import pytest

from indefinite_error import Phase, _Abort, _inject, indefinite
from tests.helpers import CALLS, SEEDS, DefiniteError, Flavor, attempt, first_fault, run

# --- Before never ran; after ran; outside _inject() nothing happens -----------


def test_inert_outside_inject(flavor: Flavor) -> None:
    ran, op = flavor.recorder()
    assert [op(i) for i in range(CALLS)] == [i * 2 for i in range(CALLS)]
    assert ran == list(range(CALLS))


@pytest.mark.parametrize("seed", SEEDS)
def test_before_never_runs_after_always_runs(flavor: Flavor, seed: int) -> None:
    ran, op = flavor.recorder()
    outcomes = run(seed, op)
    assert ran == [i for i, o in enumerate(outcomes) if o != "before"]


# --- Definite outcomes pass through; teardowns outrank the fault ----------------


def test_definite_error_passes_through_unless_faulted(flavor: Flavor) -> None:
    def boom() -> None:
        raise DefiniteError

    op = flavor.op(boom)
    seen: set[str] = set()
    for seed in SEEDS:
        with _inject(seed):
            try:
                op()
            except DefiniteError:
                seen.add("definite")
            except _Abort as e:
                seen.add(e.fault.phase)
    assert seen == {"definite", "before", "after"}


def test_a_teardown_outranks_after(flavor: Flavor) -> None:
    """A BaseException isn't an outcome: it propagates, and AFTER stays silent.

    In Rust terms: a dropped future never reaches the code after its await.
    """

    class Fatal(BaseException):
        pass

    def boom() -> None:
        raise Fatal

    op = flavor.op(boom)
    seen: set[str] = set()
    for seed in SEEDS:
        with _inject(seed):
            try:
                op()
            except _Abort as e:
                seen.add(e.fault.phase)
            except Fatal:
                seen.add("fatal")
    assert seen == {"before", "fatal"}


# --- The fault unwinds the request ------------------------------------------


def test_fault_is_invisible_to_except_exception(flavor: Flavor) -> None:
    """A retry loop can't retry past it: nothing after the fault runs."""
    _, op = flavor.recorder()
    attempts: list[int] = []

    def handler() -> str:
        for n in range(3):
            attempts.append(n)
            try:
                op(n)
            except Exception:  # noqa: S112 - the handler must not be able to retry
                continue
            else:
                return "done"
        return "gave up"

    faulted = 0
    for seed in SEEDS:
        attempts.clear()
        with _inject(seed):
            try:
                assert handler() == "done"
            except _Abort:
                faulted += 1
        assert attempts == [0], "the loop must never reach a second attempt"
    assert faulted > 0


def test_fault_runs_cleanup(flavor: Flavor) -> None:
    """The server lives on, so finally runs and locks are released, as on cancel."""
    events: list[str] = []
    commit = flavor.op(lambda: events.append("commit"), name="commit")

    for seed in SEEDS:
        events.clear()
        phase = None
        with _inject(seed):
            try:
                try:
                    commit()
                    events.append("after")
                finally:
                    events.append("finally")
            except _Abort as e:
                phase = e.fault.phase
        expected = {
            None: ["commit", "after", "finally"],
            "before": ["finally"],
            "after": ["commit", "finally"],
        }
        assert events == expected[phase]


@pytest.mark.parametrize("phase", ["before", "after"])
def test_fault_hides_what_its_caller_was_handling(flavor: Flavor, phase: Phase) -> None:
    """The fault isn't caused by the caller's error, and not by the op's either."""

    def boom() -> None:
        raise DefiniteError

    op = flavor.op(boom)

    def call_while_handling() -> None:
        try:
            raise DefiniteError  # noqa: TRY301 - the caller is mid-handling
        except DefiniteError:
            op()

    faults: list[_Abort] = []
    for seed in SEEDS:
        with _inject(seed):
            try:
                call_while_handling()
            except DefiniteError:
                pass
            except _Abort as e:
                faults.append(e)
    faulted = [e for e in faults if e.fault.phase == phase]
    assert faulted, f"no seed faulted {phase}"
    assert all(e.__cause__ is None and e.__suppress_context__ for e in faulted)


def test_fault_writes_the_line(flavor: Flavor, capfd: pytest.CaptureFixture[str]) -> None:
    _, op = flavor.recorder(name="logged")
    lines = []
    messages = []
    for seed in SEEDS:
        with _inject(seed):
            try:
                op(0)
            except _Abort as e:
                messages.append(str(e))
                lines.append(f"indefinite-error: {e.fault}\n")
    assert lines
    assert messages == [
        line.removeprefix("indefinite-error: ").removesuffix("\n") for line in lines
    ]
    assert capfd.readouterr().err == "".join(lines)


# --- Replay: same seed, same faults; sites don't interfere --------------------


def test_same_seed_same_outcomes(flavor: Flavor) -> None:
    for seed in SEEDS:
        _, a = flavor.recorder()
        _, b = flavor.recorder()
        assert run(seed, a) == run(seed, b)


def test_a_scope_depends_on_its_seed_alone(flavor: Flavor) -> None:
    """seed=41 takes the same path in its first scope or after 100 others."""
    _, op = flavor.recorder()
    first = run(41, op)
    for other in range(1000, 1100):
        run(other, op)
    assert run(41, op) == first


def test_sites_are_independent(flavor: Flavor) -> None:
    """Calls to other sites don't shift a site's decisions."""
    _, op = flavor.recorder()
    _, other = flavor.recorder(name="other")
    for seed in SEEDS:
        alone = run(seed, op)
        with _inject(seed):
            mixed = []
            for i in range(CALLS):
                attempt(other, i)
                mixed.append(attempt(op, i))
        assert mixed == alone


def test_seeds_vary_which_phases_a_site_gets(flavor: Flavor) -> None:
    """Swarm testing: per seed, a site faults never, before-only, after-only, or both."""
    _, op = flavor.recorder()
    kinds = {frozenset(run(seed, op)) - {"ok"} for seed in SEEDS}
    assert kinds == {
        frozenset(),
        frozenset({"before"}),
        frozenset({"after"}),
        frozenset({"before", "after"}),
    }


# --- What the fault says ------------------------------------------------------


def test_fault_names_the_site_call_and_seed(flavor: Flavor) -> None:
    _, op = flavor.recorder()
    fault = first_fault(lambda: op(0))
    assert fault.site == "tests.helpers.Flavor.recorder.<locals>.op"
    assert fault.n == 0
    assert str(fault) == f"{fault.phase} {fault.site}#0 (seed={fault.seed})"


def test_calls_count_every_call_faulted_or_not(flavor: Flavor) -> None:
    _, op = flavor.recorder(name="counted")
    for seed in SEEDS:
        with _inject(seed) as inj:
            for i in range(CALLS):
                attempt(op, i)
        assert inj.calls == {"counted": CALLS}


# --- Naming and decoration ----------------------------------------------------


def test_name_overrides_site(flavor: Flavor) -> None:
    _, op = flavor.recorder(name="db.commit")
    assert first_fault(lambda: op(0)).site == "db.commit"


def test_unnamed_siblings_share_a_site_named_ones_do_not(flavor: Flavor) -> None:
    """Two ops from one factory share one call counter unless named apart."""
    _, a = flavor.recorder()
    _, b = flavor.recorder()
    _, c = flavor.recorder(name="factory.c")
    with _inject(0) as inj:
        for op in (a, b, c):
            attempt(op, 0)
    assert inj.calls == {"tests.helpers.Flavor.recorder.<locals>.op": 2, "factory.c": 1}


@pytest.mark.parametrize("name", [None, "named"])
def test_stacked_indefinite_rejected(flavor: Flavor, name: str | None) -> None:
    decorated = flavor.decorate(_documented, name=name)
    with pytest.raises(TypeError, match="already applied to"):
        indefinite(decorated)


def _documented(x: int) -> int:
    """Doc."""
    return x


_documented.marker = "kept"  # ty: ignore[unresolved-attribute]


def test_preserves_metadata(flavor: Flavor) -> None:
    decorated: Any = flavor.decorate(_documented)
    assert decorated.__name__ == "_documented"
    assert decorated.__doc__ == "Doc."
    assert decorated.marker == "kept", "function attributes survive, as with functools.wraps"
    assert inspect.signature(decorated) == inspect.signature(_documented)
    assert inspect.iscoroutinefunction(decorated) == (flavor.kind == "async")
