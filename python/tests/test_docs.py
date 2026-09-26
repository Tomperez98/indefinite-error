"""The README is a contract: its code runs and its examples are real."""

from __future__ import annotations

import ast
import re
from pathlib import Path
from typing import Any

import pytest

from indefinite_error import _Abort, _inject, _report, indefinite
from indefinite_error.asgi import IndefiniteMiddleware

README = (Path(__file__).parent.parent / "README.md").read_text(encoding="utf-8")
BLOCKS = re.findall(r"```python\n(.*?)```", README, re.DOTALL)


def _block(marker: str) -> str:
    matches = [b for b in BLOCKS if marker in b]
    assert len(matches) == 1, f"expected one README block containing {marker!r}"
    return matches[0]


def _exec(source: str) -> dict[str, Any]:
    namespace: dict[str, Any] = {"__name__": "readme"}  # a module name, for default sites
    exec(compile(source, "README.md", "exec"), namespace)  # noqa: S102 - our own README
    return namespace


@pytest.mark.parametrize("block", BLOCKS, ids=lambda b: b.splitlines()[0][:40])
def test_every_block_parses(block: str) -> None:
    ast.parse(block)


def test_quickstart_installs_the_middleware() -> None:
    """The first block runs as written, given an ``app`` to wrap."""
    app = object()
    namespace = _exec("app = object()\n" + _block("@indefinite  # mark the boundary write"))
    assert isinstance(namespace["app"], IndefiniteMiddleware)
    assert type(namespace["app"].app) is type(app)
    assert namespace["commit"].__indefinite_site__ == "readme.commit"


def test_fault_line_example_is_real() -> None:
    """The README's line is what seed 13 writes on the fifth call to app.get."""
    get = indefinite(name="app.get")(lambda: None)
    with _inject(13):
        for _ in range(4):
            get()
        with pytest.raises(_Abort) as info:
            get()
    shown = re.search(r"```text\n(indefinite-error: .*\n)```", README)
    assert shown is not None
    assert shown.group(1).encode() == _report(info.value.fault)
