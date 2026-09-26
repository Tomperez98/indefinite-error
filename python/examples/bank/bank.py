"""A tiny bank: deposits into accounts, stored in SQLite.

Two ways to deposit. ``POST /deposits/unkeyed`` just adds the amount;
``POST /deposits`` carries an idempotency key and applies each key once. Under
indefinite errors a retrying client double-counts with the first and never
with the second -- which the tests in ``test_bank.py`` show.

Run it with faults on, and send a seed per request:

    INDEFINITE_ERRORS=1 uv run uvicorn bank:app --app-dir examples/bank
"""

from __future__ import annotations

import os
import sqlite3

from fastapi import FastAPI
from pydantic import BaseModel

from indefinite_error import indefinite
from indefinite_error.asgi import IndefiniteMiddleware


class Store:
    """The durable state. Its writes are the boundary where outcomes get lost."""

    def __init__(self, path: str = ":memory:") -> None:
        # check_same_thread=False: a test client may run the app on another thread.
        self.db = sqlite3.connect(path, check_same_thread=False)
        with self.db:
            self.db.execute(
                "CREATE TABLE IF NOT EXISTS accounts (id TEXT PRIMARY KEY, balance INT)"
            )
            self.db.execute("CREATE TABLE IF NOT EXISTS applied (key TEXT PRIMARY KEY)")

    # Mark each write that talks to the outside world. A fault lands right
    # before it (the write never happened) or right after it (it committed, but
    # the response is lost).

    @indefinite(name="store.deposit")
    def deposit(self, account: str, amount: int) -> None:
        with self.db:
            self._add(account, amount)

    @indefinite(name="store.deposit_once")
    def deposit_once(self, key: str, account: str, amount: int) -> None:
        with self.db:  # one transaction: the key and the money, or neither
            new = self.db.execute("INSERT INTO applied VALUES (?) ON CONFLICT DO NOTHING", (key,))
            if new.rowcount == 1:
                self._add(account, amount)

    def balance(self, account: str) -> int:
        row = self.db.execute("SELECT balance FROM accounts WHERE id = ?", (account,)).fetchone()
        return 0 if row is None else row[0]

    def _add(self, account: str, amount: int) -> None:
        self.db.execute(
            "INSERT INTO accounts VALUES (?, ?)"
            " ON CONFLICT (id) DO UPDATE SET balance = balance + excluded.balance",
            (account, amount),
        )


class Deposit(BaseModel):
    account: str
    amount: int


class KeyedDeposit(Deposit):
    key: str


def create_app(store: Store, *, indefinite_errors: bool) -> FastAPI:
    app = FastAPI()

    @app.post("/deposits/unkeyed")
    async def deposit_unkeyed(body: Deposit) -> dict[str, str]:
        store.deposit(body.account, body.amount)
        return {"status": "ok"}

    @app.post("/deposits")
    async def deposit(body: KeyedDeposit) -> dict[str, str]:
        store.deposit_once(body.key, body.account, body.amount)
        return {"status": "ok"}

    @app.get("/accounts/{account}")
    async def balance(account: str) -> dict[str, int]:
        return {"balance": store.balance(account)}

    if indefinite_errors:  # never in production: any caller could fault the server
        app.add_middleware(IndefiniteMiddleware)
    return app


# The one place the environment is read: `uvicorn bank:app`.
app = create_app(
    Store(os.environ.get("BANK_DB", ":memory:")),
    indefinite_errors=os.environ.get("INDEFINITE_ERRORS") == "1",
)
