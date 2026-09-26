"""WSGI middleware: each request carrying ``X-Indefinite-Seed`` runs inside its own injection.

    app = IndefiniteMiddleware(app)

A fault unwinds its request to here. WSGI has no way to drop a connection
before its response starts -- a server turns an exception into a ``500``, and
the headers are already committed once they start -- so the middleware holds
the app's ``start_response`` until the first body chunk and answers ``500``
with ``X-Indefinite-Fault`` naming the fault. A fault after the response has
committed re-raises, and the server closes the connection mid-response.

The seed must be exactly one decimal int64 header value
(``spec/seed-header.tsv``); anything else gets a ``400``, and the app never
sees the request. A WSGI server folds repeated headers into one comma-separated
value, which no decimal int64 matches, so a repeated seed gets the ``400`` the
spec requires -- though the middleware can't tell a repeat from a single
malformed value. Requests without the header pass through untouched. Never
install it in production: any caller could fault it.
"""

from __future__ import annotations

import re
from typing import TYPE_CHECKING, Any

from indefinite_error import _INT64, _aborted, _inject

if TYPE_CHECKING:
    from collections.abc import Callable, Iterable, MutableMapping

    type Environ = MutableMapping[str, Any]
    type Write = Callable[[bytes], Any]
    type StartResponse = Callable[[str, list[tuple[str, str]], Any], Write]
    type WSGIApp = Callable[[Environ, StartResponse], Iterable[bytes]]

__all__ = ["IndefiniteMiddleware"]

SEED_HEADER = "HTTP_X_INDEFINITE_SEED"
FAULT_HEADER = "X-Indefinite-Fault"

# A decimal int64 has at most 19 digits: the header is untrusted input.
_SEED = re.compile(r"-?[0-9]{1,19}")

_TEXT_HEADERS: list[tuple[str, str]] = [("content-type", "text/plain")]
_MALFORMED = b"X-Indefinite-Seed must be a decimal int64"


class IndefiniteMiddleware:
    """Run each request carrying ``X-Indefinite-Seed`` inside its own injection."""

    def __init__(self, app: WSGIApp) -> None:
        self.app = app

    def __call__(self, environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        value = environ.get(SEED_HEADER)
        if value is None:
            return self.app(environ, start_response)
        seed = _parse_seed(value)
        if seed is None:
            return _respond(start_response, "400 Bad Request", _TEXT_HEADERS, _MALFORMED)
        return _guarded(self.app, environ, start_response, seed)


def _parse_seed(value: str) -> int | None:
    """The seed, if ``value`` is exactly one decimal int64 (``spec/seed-header.tsv``)."""
    if not _SEED.fullmatch(value):
        return None
    seed = int(value)
    return seed if seed in _INT64 else None


def _guarded(
    app: WSGIApp, environ: Environ, start_response: StartResponse, seed: int
) -> Iterable[bytes]:
    """The app's response, with one injection open across the whole body.

    ``start_response`` isn't forwarded until the first body chunk: until then a
    fault can still answer ``500``. Afterwards the response has committed, so a
    fault propagates and the server closes the connection.
    """
    pending: tuple[str, list[tuple[str, str]], Any] | None = None
    committed = False  # pragma: no mutate (None is just as falsy)
    server_write: Write | None = None  # pragma: no mutate (only read after commit() sets it)

    def commit() -> None:
        nonlocal committed, server_write
        if committed:
            return
        assert pending is not None, "the app produced a body before calling start_response"
        status, headers, exc_info = pending
        server_write = start_response(status, headers, exc_info)
        committed = True

    def write(data: bytes) -> None:
        commit()
        assert server_write is not None  # commit() just set it
        server_write(data)

    def capture(status: str, headers: list[tuple[str, str]], exc_info: Any = None) -> Write:
        nonlocal pending
        pending = (status, headers, exc_info)
        return write

    with _inject(seed):
        try:
            iterable = app(environ, capture)
            try:
                for chunk in iterable:
                    commit()
                    yield chunk
                commit()  # an empty body still has to send its headers
            finally:
                close = getattr(iterable, "close", None)
                if close is not None:
                    close()
        except BaseException as exc:
            fault = _aborted(exc)
            if fault is None or committed:
                raise  # not ours, or too late to answer: the server closes it
            yield from _respond(
                start_response, "500 Internal Server Error", [(FAULT_HEADER, str(fault))], b""
            )


def _respond(
    start_response: StartResponse, status: str, headers: list[tuple[str, str]], body: bytes
) -> list[bytes]:
    start_response(status, headers, None)
    return [body]
