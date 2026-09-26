"""A retrying client must keep the bank's books right under indefinite errors.

Each run makes DEPOSITS deposits of 1 against a fresh bank, every request with
its own seed, retrying whenever the outcome is indefinite -- as a real client
would after a timeout. Then it checks one invariant: the balance is exactly
DEPOSITS.

    uv run pytest examples/bank
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

import pytest
from bank import Store, create_app
from fastapi.testclient import TestClient

RUNS = range(50)
DEPOSITS = 20
ATTEMPTS = 30  # a seed may fault half its requests: retry until one gets through


def seed(run: int, step: int, attempt: int) -> int:
    """One seed per request, derived from the run: replay a run, replay its faults."""
    return run * 1_000_000 + step * 1_000 + attempt


@dataclass
class Run:
    """What one run did: its final balance, and every fault the server reported."""

    balance: int = 0
    faults: list[str] = field(default_factory=list)

    def ok(self) -> bool:
        return self.balance == DEPOSITS

    def __str__(self) -> str:
        return f"balance {self.balance}, expected {DEPOSITS}; faults: {self.faults}"


def run_deposits(run: int, path: str, body: Any) -> Run:
    client = TestClient(create_app(Store(), indefinite_errors=True))
    result = Run()
    for step in range(DEPOSITS):
        for attempt in range(ATTEMPTS):
            headers = {"X-Indefinite-Seed": str(seed(run, step, attempt))}
            response = client.post(path, json=body(step), headers=headers)
            if response.status_code == 200:
                break
            # Indefinite: it may or may not have happened. The fault header is
            # for us, debugging; the client logic must not read it. Retry.
            assert response.status_code == 500, response.text
            result.faults.append(response.headers["X-Indefinite-Fault"])
        else:
            pytest.fail(f"step {step} never got through in {ATTEMPTS} attempts")
    result.balance = client.get("/accounts/alice").json()["balance"]
    return result


def keyed(step: int) -> dict[str, Any]:
    # The key is fixed per deposit, not per attempt: a retry is the same deposit.
    return {"key": f"deposit-{step}", "account": "alice", "amount": 1}


def unkeyed(step: int) -> dict[str, Any]:
    return {"account": "alice", "amount": 1}


@pytest.mark.parametrize("run", RUNS)
def test_keyed_deposits_count_exactly_once(run: int) -> None:
    result = run_deposits(run, "/deposits", keyed)
    assert result.ok(), f"run {run}: {result}"


def test_unkeyed_deposits_double_count_and_the_faults_say_why() -> None:
    """The bug this library exists to find, found: an AFTER fault, then a retry."""
    broken = {
        run: r for run in RUNS if not (r := run_deposits(run, "/deposits/unkeyed", unkeyed)).ok()
    }
    assert broken, "no run double-counted: the faults never reached store.deposit?"
    for result in broken.values():
        assert result.balance > DEPOSITS, "retries only ever add"
        assert any(f.startswith("after store.deposit#") for f in result.faults)
