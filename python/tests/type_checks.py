"""Signatures pinned at type-check time: ``ty check`` is the test runner here.

Nothing here runs. A negative check is a line that must fail to type-check:
its ``ty: ignore`` is an error if unused (see ``[tool.ty.rules]``), so the
line stops compiling cleanly the moment the type gets looser.
"""

from __future__ import annotations

from collections.abc import Callable
from types import CoroutineType
from typing import Any, Never, assert_type

from indefinite_error import indefinite


def by_position(x: int, /) -> str:
    return str(x)


def by_keyword(*, x: int) -> str:
    return str(x)


async def async_by_position(x: int, /) -> str:
    return str(x)


def _checks() -> None:
    # The decorator keeps the exact signature, sync or async, bare or named.
    assert_type(indefinite(by_position), Callable[[int], str])
    assert_type(indefinite(name="x")(by_position), Callable[[int], str])
    assert_type(indefinite(by_position, name="x"), Callable[[int], str])
    assert_type(indefinite(async_by_position), Callable[[int], CoroutineType[Any, Any, str]])

    # Keyword-only stays keyword-only; the return type isn't widened to Any.
    indefinite(by_keyword)(x=1)
    indefinite(by_keyword)(1)  # ty: ignore[missing-argument, too-many-positional-arguments]
    _: int = indefinite(by_position)(1)  # ty: ignore[invalid-assignment]


def _bare_call() -> None:
    # A bare call is a bug, so its type is Never. (Its own function: code after
    # a Never is unreachable, and ty doesn't check unreachable code.)
    assert_type(indefinite(), Never)
