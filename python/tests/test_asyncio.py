"""Faults under asyncio: one _inject() per task, and they don't interfere.

That a fault ends only its own request is in ``test_asgi.py``.
"""

from __future__ import annotations

import asyncio
from typing import TYPE_CHECKING

from indefinite_error import _Abort, _inject, indefinite
from tests.helpers import CALLS, SEEDS, Outcome

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable

type AsyncOp = Callable[[int], Awaitable[int]]


def _async_recorder() -> tuple[list[int], AsyncOp]:
    ran: list[int] = []

    @indefinite(name="async.op")
    async def op(i: int) -> int:
        await asyncio.sleep(0)
        ran.append(i)
        return i * 2

    return ran, op


async def _scope(seed: int, op: AsyncOp) -> list[Outcome]:
    with _inject(seed):
        out: list[Outcome] = []
        for i in range(CALLS):
            try:
                assert await op(i) == i * 2
                out.append("ok")
            except _Abort as e:
                out.append(e.fault.phase)
        return out


def test_concurrent_scopes_do_not_interfere() -> None:
    """One _inject() per task, all overlapping: each gets the path it gets alone."""
    _, op = _async_recorder()
    alone = {s: asyncio.run(_scope(s, op)) for s in SEEDS}

    async def overlapping() -> list[list[Outcome]]:
        return await asyncio.gather(*(_scope(s, op) for s in [*SEEDS, *SEEDS]))

    for seed, got in zip([*SEEDS, *SEEDS], asyncio.run(overlapping()), strict=True):
        assert got == alone[seed]
