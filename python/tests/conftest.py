from __future__ import annotations

import asyncio
from typing import TYPE_CHECKING

import pytest
from hypothesis import settings

from tests.helpers import Flavor

if TYPE_CHECKING:
    from collections.abc import Iterator

# PR CI: the same examples every run, so a red build is a real regression.
settings.register_profile("default", max_examples=200, deadline=None, derandomize=True)
# Nightly (`--hypothesis-profile=nightly`): fresh examples; a failure prints its replay blob.
settings.register_profile("nightly", max_examples=5000, deadline=None, print_blob=True)
settings.load_profile("default")


@pytest.fixture(params=["sync", "async"])
def flavor(request: pytest.FixtureRequest) -> Iterator[Flavor]:
    if request.param == "sync":
        yield Flavor("sync", None)
        return
    with asyncio.Runner() as runner:
        yield Flavor("async", runner)
