"""ASGI middleware: each request carrying ``X-Indefinite-Seed`` runs inside its own injection.

    app = IndefiniteMiddleware(app)

A fault unwinds its request to here, and the middleware answers ``500`` with
``X-Indefinite-Fault`` naming it. ASGI gives an app no way to drop a
connection before its response starts -- servers answer ``500`` for an app
that raises -- so this is the closest a request can come to vanishing. If the
response had already started, the middleware re-raises, and the server closes
the connection mid-response.

The seed must be exactly one decimal int64 header value
(``spec/seed-header.tsv``); anything else gets a ``400``, and the app never
sees the request. Requests without the header, and non-HTTP scopes (lifespan,
websocket), pass through untouched. Never install it in production: any
caller could fault it.
"""

from __future__ import annotations

import re
from typing import TYPE_CHECKING, Any

from indefinite_error import _INT64, Fault, _Abort, _inject

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable, MutableMapping

    type Message = MutableMapping[str, Any]
    type Receive = Callable[[], Awaitable[Message]]
    type Send = Callable[[Message], Awaitable[None]]
    type ASGIApp = Callable[[MutableMapping[str, Any], Receive, Send], Awaitable[None]]

__all__ = ["IndefiniteMiddleware"]

SEED_HEADER = b"x-indefinite-seed"
FAULT_HEADER = b"x-indefinite-fault"

# A decimal int64 has at most 19 digits: the header is untrusted input.
_SEED = re.compile(rb"-?[0-9]{1,19}")


class IndefiniteMiddleware:
    """Run each request carrying ``X-Indefinite-Seed`` inside its own injection."""

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    async def __call__(self, scope: MutableMapping[str, Any], receive: Receive, send: Send) -> None:
        values = _seed_headers(scope) if scope["type"] == "http" else []
        if not values:
            return await self.app(scope, receive, send)
        seed = _parse_seed(values)
        if seed is None:
            body = b"X-Indefinite-Seed must be a decimal int64"
            return await _respond(send, 400, [(b"content-type", b"text/plain")], body)

        started = False

        async def tracking_send(message: Message) -> None:
            nonlocal started
            started = started or message["type"] == "http.response.start"
            await send(message)

        with _inject(seed):
            try:
                return await self.app(scope, receive, tracking_send)
            except BaseException as exc:
                fault = _aborted(exc)
                if fault is None or started:
                    raise  # not ours, or too late to answer: the server closes it
        await _respond(send, 500, [(FAULT_HEADER, str(fault).encode())], b"")
        return None


def _seed_headers(scope: MutableMapping[str, Any]) -> list[bytes]:
    """Every value of the seed header. ASGI lowercases header names."""
    return [value for name, value in scope.get("headers", ()) if name == SEED_HEADER]


def _parse_seed(values: list[bytes]) -> int | None:
    """The seed, if ``values`` is exactly one decimal int64 (``spec/seed-header.tsv``)."""
    if len(values) != 1 or not _SEED.fullmatch(values[0]):
        return None
    seed = int(values[0])
    return seed if seed in _INT64 else None


def _aborted(exc: BaseException) -> Fault | None:
    """The fault that ended this request, if one did.

    A task group wraps it in a ``BaseExceptionGroup``. Alongside ordinary
    exceptions the fault still ended the request; alongside a teardown
    (``KeyboardInterrupt``, ``SystemExit``, cancellation) the teardown wins.
    """
    if isinstance(exc, _Abort):
        return exc.fault
    if not isinstance(exc, BaseExceptionGroup):
        return None
    aborts, rest = exc.split(_Abort)
    if aborts is None:
        return None
    if rest is not None and rest.split(Exception)[1] is not None:
        return None  # a teardown rides along: it outranks the fault
    first: BaseException = aborts
    while isinstance(first, BaseExceptionGroup):
        first = first.exceptions[0]
    assert isinstance(first, _Abort), f"split(_Abort) yielded {first!r}"
    return first.fault


async def _respond(
    send: Send, status: int, headers: list[tuple[bytes, bytes]], body: bytes
) -> None:
    await send({"type": "http.response.start", "status": status, "headers": headers})
    await send({"type": "http.response.body", "body": body})
