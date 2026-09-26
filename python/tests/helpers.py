"""Shared test helpers. They fail the test themselves; none returns an error."""

from __future__ import annotations

import asyncio
import contextvars
import functools
import json
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING, Any, Literal

import pytest

from indefinite_error import (
    Fault,
    Mode,
    Phase,
    _Abort,
    _inject,
    _mode as mode_of,  # tests pick seeds by what they choose
    indefinite,
)

if TYPE_CHECKING:
    from collections.abc import Callable

SEEDS = range(300)
CALLS = 50


def _spec_dir() -> Path:
    """The repository's ``spec/``: the contract every implementation is tested against.

    Found by walking up, so it resolves from ``tests/`` and from mutmut's copy alike.
    """
    here = Path(__file__).resolve()
    for parent in here.parents:
        if (parent / "spec" / "README.md").is_file():
            return parent / "spec"
    msg = f"no spec/README.md above {here}: tests run from a repository checkout"
    raise FileNotFoundError(msg)


SPEC = _spec_dir()


def spec_rows(name: str) -> list[list[str]]:
    """The tab-separated rows of ``spec/<name>``, without its ``#`` comments."""
    text = (SPEC / name).read_text(encoding="utf-8")
    return [line.split("\t") for line in text.splitlines() if line and not line.startswith("#")]


def spec_site(cell: str) -> str:
    """A site cell: a JSON string."""
    site = json.loads(cell)
    assert isinstance(site, str), f"site cell {cell!r} is not a JSON string"
    return site


type Op = Callable[[int], int]
type Outcome = Literal["ok", "before", "after"]
type Kind = Literal["sync", "async"]


class DefiniteError(Exception):
    """A failure the operation itself reports: a value its caller handles."""


def attempt(op: Op, i: int) -> Outcome:
    """'ok', or the injected phase: one call is one request.

    Catching ``_Abort`` here stands in for the middleware, its only catcher.
    """
    try:
        result = op(i)
    except _Abort as e:
        return e.fault.phase
    assert result == i * 2, f"op({i}) returned {result}"
    return "ok"


def run(seed: int, op: Op, calls: int = CALLS) -> list[Outcome]:
    """The outcomes of ``calls`` calls to ``op`` in one injection."""
    with _inject(seed):
        return [attempt(op, i) for i in range(calls)]


def first_fault(call: Callable[[], object], phase: Phase | None = None) -> Fault:
    """The first fault ``call()`` hits across ``SEEDS`` (of ``phase``, if given)."""
    for seed in SEEDS:
        with _inject(seed):
            try:
                call()
            except _Abort as e:
                if phase is None or e.fault.phase == phase:
                    return e.fault
    pytest.fail(f"no seed in {SEEDS} faulted {phase or 'at all'}")


def seed_where(site: str, mode: Mode) -> int:
    """The first seed that gives ``site`` this ``mode``."""
    for seed in SEEDS:
        if mode_of(seed, site) == mode:
            return seed
    pytest.fail(f"no seed in {SEEDS} gives {site} mode {mode}")


@dataclass(frozen=True, slots=True)
class Flavor:
    """How an operation is defined -- ``def`` or ``async def`` -- behind one sync call.

    The two wrappers are two implementations of one contract; tests that take
    the ``flavor`` fixture hold both to it.
    """

    kind: Kind
    runner: asyncio.Runner | None

    def decorate(self, body: Callable[..., Any], *, name: str | None = None) -> Callable[..., Any]:
        """``@indefinite`` over ``body``, as a def or an async def with body's name."""
        target = body
        if self.kind == "async":

            @functools.wraps(body)
            async def target(*args: Any, **kwargs: Any) -> Any:
                await asyncio.sleep(0)  # a real suspension point, like I/O
                return body(*args, **kwargs)

        return indefinite(target) if name is None else indefinite(name=name)(target)

    def call(self, decorated: Callable[..., Any], *args: Any, **kwargs: Any) -> Any:
        """Call ``decorated``; an async one runs to completion on this flavor's loop.

        The task gets a copy of the caller's context, so it sees its ``_inject()``.
        """
        if self.runner is None:
            return decorated(*args, **kwargs)
        return self.runner.run(decorated(*args, **kwargs), context=contextvars.copy_context())

    def op(self, body: Callable[..., Any], *, name: str | None = None) -> Callable[..., Any]:
        """``decorate`` then ``call``: a plain sync callable in either flavor."""
        decorated = self.decorate(body, name=name)
        return functools.partial(self.call, decorated)

    def recorder(self, *, name: str | None = None) -> tuple[list[int], Op]:
        """An op that records each real execution and returns ``i * 2``."""
        ran: list[int] = []

        def op(i: int) -> int:
            ran.append(i)
            return i * 2

        return ran, self.op(op, name=name)
