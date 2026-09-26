"""ASGI middleware: each request carrying ``X-Indefinite-Seed`` runs inside its own injection.

    app = IndefiniteMiddleware(app)

A fault unwinds its request to here, and the middleware answers ``500`` with
``X-Indefinite-Fault`` naming it. ASGI gives an app no way to drop a
connection before its response starts -- servers answer ``500`` for an app
that raises -- so this is the closest a request can come to vanishing. If the
response had already started, the middleware re-raises, and the server closes
the connection mid-response.

Requests without the header, and non-HTTP scopes (lifespan, websocket), pass
through untouched. Never install it in production: any caller could fault it.
"""

from __future__ import annotations

import re
from typing import TYPE_CHECKING, Any

from indefinite_error import Fault, _Abort, _inject

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable, MutableMapping

    type Message = MutableMapping[str, Any]
    type Receive = Callable[[], Awaitable[Message]]
    type Send = Callable[[Message], Awaitable[None]]
    type ASGIApp = Callable[[MutableMapping[str, Any], Receive, Send], Awaitable[None]]

__all__ = ["IndefiniteMiddleware"]

SEED_HEADER = b"x-indefinite-seed"
FAULT_HEADER = b"x-indefinite-fault"

# A decimal integer, bounded: the header is untrusted input.
_SEED = re.compile(rb"-?[0-9]{1,64}")


class IndefiniteMiddleware:
    """Run each request carrying ``X-Indefinite-Seed`` inside its own injection."""

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    async def __call__(self, scope: MutableMapping[str, Any], receive: Receive, send: Send) -> None:
        raw = _seed_header(scope) if scope["type"] == "http" else None
        if raw is None:
            return await self.app(scope, receive, send)
        if not _SEED.fullmatch(raw):
            body = b"X-Indefinite-Seed must be a decimal integer"
            return await _respond(send, 400, [(b"content-type", b"text/plain")], body)

        started = False

        async def tracking_send(message: Message) -> None:
            nonlocal started
            started = started or message["type"] == "http.response.start"
            await send(message)

        with _inject(int(raw)):
            try:
                return await self.app(scope, receive, tracking_send)
            except BaseException as exc:
                fault = _aborted(exc)
                if fault is None or started:
                    raise  # not ours, or too late to answer: the server closes it
        await _respond(send, 500, [(FAULT_HEADER, str(fault).encode())], b"")
        return None


def _seed_header(scope: MutableMapping[str, Any]) -> bytes | None:
    """The seed header's value, or ``None``. ASGI lowercases header names."""
    for name, value in scope.get("headers", ()):
        if name == SEED_HEADER:
            return value
    return None


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
