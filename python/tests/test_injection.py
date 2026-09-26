"""``_inject()`` and the ``Injection`` it yields: scope, visibility, bookkeeping."""

from __future__ import annotations

import asyncio
import contextlib
import contextvars
import sys
import threading

import pytest
from hypothesis import given, strategies as st

from indefinite_error import Injection, _Abort, _decide as decide, _inject, indefinite
from tests.helpers import CALLS, SEEDS, attempt, mode_of, run, seed_where

# --- The seed -----------------------------------------------------------------


@pytest.mark.parametrize("seed", ["1", 1.0, True, None, 2**63, -(2**63) - 1])
def test_seed_must_be_int64(seed: object) -> None:
    """The middleware parses the header; anything else here is its bug."""
    with (
        pytest.raises(AssertionError, match="seed"),
        _inject(seed),  # ty: ignore[invalid-argument-type]
    ):
        pass


@given(st.integers(-(2**63), 2**63 - 1))
def test_any_int64_is_a_seed(seed: int) -> None:
    """Negative, zero, and the extremes: all valid, all replayable."""
    op = indefinite(name="any")(lambda i: i * 2)
    first = run(seed, op, calls=5)
    assert run(seed, op, calls=5) == first


# --- Scope --------------------------------------------------------------------


def test_nested_inject_panics() -> None:
    with (
        _inject(1),
        pytest.raises(RuntimeError, match="seeded 2 inside one seeded 1"),
        _inject(2),
    ):
        pass


def test_inject_resets_on_exit() -> None:
    ran: list[int] = []
    op = indefinite(name="op")(lambda i: ran.append(i) or i * 2)
    with pytest.raises(ValueError, match="boom"), _inject(1) as inj:
        raise ValueError("boom")  # noqa: EM101
    assert inj.closed
    assert op(1) == 2
    assert ran == [1]


def test_task_outliving_inject_gets_no_faults() -> None:
    """Outside _inject() the decorator does nothing, even in a leaked task."""
    ran: list[int] = []

    @indefinite
    async def op(i: int) -> int:
        ran.append(i)
        return i

    async def main(seed: int) -> tuple[Injection, list[int]]:
        gate = asyncio.Event()

        async def leaked() -> list[int]:
            await gate.wait()
            return [await op(i) for i in range(CALLS)]

        with _inject(seed) as inj:
            task = asyncio.create_task(leaked())
        gate.set()
        return inj, await task

    for seed in SEEDS:
        inj, results = asyncio.run(main(seed))
        assert inj.closed
        assert results == list(range(CALLS))
        assert inj.calls == {}


def test_leaked_task_may_start_its_own_inject() -> None:
    async def main() -> Injection:
        gate = asyncio.Event()

        async def leaked() -> Injection:
            await gate.wait()
            with _inject(2) as inner:
                return inner

        with _inject(1):
            task = asyncio.create_task(leaked())
        gate.set()
        return await task

    assert asyncio.run(main()).seed == 2


# --- The Injection object -----------------------------------------------------


def test_calls_and_modes_show_which_sites_were_reached() -> None:
    reached = indefinite(name="reached")(lambda i: i * 2)
    indefinite(name="unreached")(lambda i: i * 2)
    with _inject(3) as inj:
        for i in range(CALLS):
            attempt(reached, i)
    assert inj.calls == {"reached": CALLS}
    assert inj.modes == {"reached": mode_of(3, "reached")}
    assert inj.seed == 3
    assert inj.rate in {0.01, 0.05, 0.2, 0.5}


def test_injection_is_read_only() -> None:
    with _inject(1) as inj:
        for attr in ("seed", "rate", "calls", "modes", "closed"):
            with pytest.raises(AttributeError):
                setattr(inj, attr, 2)
        assert not hasattr(inj, "next")
        inj.calls["x"] = 99  # a copy: can't shift the replay
        inj.modes["x"] = "both"
        assert "x" not in inj.calls
        assert "x" not in inj.modes


def test_repr_shows_seed_rate_and_sites() -> None:
    a = indefinite(name="repr.a")(lambda i: i * 2)
    b = indefinite(name="repr.b")(lambda i: i * 2)
    with _inject(3) as inj:
        attempt(b, 0)
        attempt(a, 0)
    modes = f"repr.a={mode_of(3, 'repr.a')}, repr.b={mode_of(3, 'repr.b')}"
    assert repr(inj) == f"Injection(seed=3, rate={inj.rate}, sites=[{modes}])"


def test_a_constructed_injection_is_inert() -> None:
    """Only _inject() activates one; building it by hand changes nothing."""
    ran: list[int] = []
    op = indefinite(name="op")(lambda i: ran.append(i) or i * 2)
    inj = Injection(1)
    assert [op(i) for i in range(CALLS)] == [i * 2 for i in range(CALLS)]
    assert inj.calls == {}


# --- Threads ------------------------------------------------------------------


def test_thread_with_copied_context_sees_the_injection() -> None:
    op = indefinite(name="threaded")(lambda i: i * 2)
    got: list[str] = []
    for seed in SEEDS:
        expected = run(seed, op)
        got.clear()
        with _inject(seed):
            ctx = contextvars.copy_context()
            t = threading.Thread(
                target=ctx.run, args=(lambda: got.extend(attempt(op, i) for i in range(CALLS)),)
            )
            t.start()
            t.join()
        assert got == expected


def test_thread_without_context_is_a_silent_miss() -> None:
    """The documented trap -- except on builds where threads inherit the context."""
    op = indefinite(name="missed")(lambda i: i * 2)
    with _inject(0) as inj:
        t = threading.Thread(target=lambda: [attempt(op, i) for i in range(CALLS)])
        t.start()
        t.join()
    inherits = getattr(sys.flags, "thread_inherit_context", False)  # 3.14+; on by default in 3.14t
    assert ("missed" in inj.calls) == bool(inherits)


def test_to_thread_sees_the_injection() -> None:
    op = indefinite(name="to_thread")(lambda i: i * 2)

    async def main() -> Injection:
        with _inject(0) as inj:
            for i in range(CALLS):
                with contextlib.suppress(_Abort):
                    await asyncio.to_thread(op, i)
        return inj

    assert asyncio.run(main()).calls == {"to_thread": CALLS}


def test_threads_sharing_one_injection_lose_no_calls() -> None:
    """Every concurrent call gets its own n, and the faults are exactly the seed's.

    Only a free-threaded build (3.14t) races hard enough to fail without the lock.
    """
    site, threads, per_thread = "hot", 8, 10_000
    op = indefinite(name=site)(lambda: None)
    seed = seed_where(site, "both")
    barrier = threading.Barrier(threads)
    faulted: list[int] = []  # list.append is atomic, even free-threaded

    def work() -> None:
        barrier.wait()
        for _ in range(per_thread):
            try:
                op()
            except _Abort as e:
                faulted.append(e.fault.n)

    with _inject(seed) as inj:
        workers = [
            threading.Thread(target=contextvars.copy_context().run, args=(work,))
            for _ in range(threads)
        ]
        for w in workers:
            w.start()
        for w in workers:
            w.join()
    total = threads * per_thread
    assert inj.calls == {site: total}
    expected = [n for n in range(total) if decide(seed, inj.rate, "both", site, n)]
    assert sorted(faulted) == expected
