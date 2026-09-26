"""The shipped ASGI middleware: one injection per request, and its table."""

from __future__ import annotations

import asyncio
import json
from typing import TYPE_CHECKING, Any

import pytest

import indefinite_error
from indefinite_error import Fault, _Abort, indefinite
from indefinite_error.asgi import IndefiniteMiddleware
from tests.helpers import DefiniteError, spec_rows

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable, MutableMapping

# The shapes Starlette and friends use: an app of theirs type-checks here too.
type Scope = MutableMapping[str, Any]
type Message = MutableMapping[str, Any]
type Receive = Callable[[], Awaitable[Message]]
type Send = Callable[[Message], Awaitable[None]]
type App = Callable[[Scope, Receive, Send], Awaitable[None]]

SEEDS = range(300)


def _call(asgi: App, scope: Scope) -> list[Message]:
    sent: list[Message] = []

    async def send(message: Message) -> None:
        sent.append(message)

    async def receive() -> Message:
        return {"type": "http.request"}

    async def main() -> None:
        await asgi(scope, receive, send)

    asyncio.run(main())
    return sent


def _http(seed: int | bytes | None) -> Scope:
    headers = [(b"accept", b"*/*")]
    if seed is not None:
        headers.append((b"x-indefinite-seed", seed if isinstance(seed, bytes) else b"%d" % seed))
    return {"type": "http", "headers": headers}


def _writing_app() -> tuple[list[int], App]:
    state: list[int] = []

    @indefinite(name="app.write")
    async def write(x: int) -> None:
        state.append(x)

    async def app(scope: Scope, receive: Receive, send: Send) -> None:
        await write(1)
        await send({"type": "http.response.start", "status": 201, "headers": []})
        await send({"type": "http.response.body", "body": b"created"})

    return state, app


def _response(sent: list[Message]) -> tuple[int, str | None]:
    """The status, and the fault the X-Indefinite-Fault header names."""
    start, *_ = sent
    fault = dict(start["headers"]).get(b"x-indefinite-fault")
    return start["status"], None if fault is None else fault.decode()


# --- The table -----------------------------------------------


def test_rows_match_the_table() -> None:
    """No fault: the real response; before: 500, unchanged; after: 500, changed."""
    state, app = _writing_app()
    asgi = IndefiniteMiddleware(app)
    rows = set()
    for seed in SEEDS:
        state.clear()
        status, fault = _response(_call(asgi, _http(seed)))
        phase = None if fault is None else fault.split(" ", 1)[0]
        rows.add((status, phase, state == [1]))
        if fault is not None:
            assert fault.endswith(f"(seed={seed})")
    assert rows == {(201, None, True), (500, "before", False), (500, "after", True)}


def test_the_fault_response_has_no_body() -> None:
    _, app = _writing_app()
    asgi = IndefiniteMiddleware(app)
    for seed in SEEDS:
        sent = _call(asgi, _http(seed))
        if _response(sent)[0] == 500:
            assert sent[1:] == [{"type": "http.response.body", "body": b""}]
            return
    pytest.fail("no seed faulted")


def test_the_seed_is_a_whole_request_input() -> None:
    """The same seed takes the same path at request 1 or request 100."""
    _, app = _writing_app()
    asgi = IndefiniteMiddleware(app)
    first = [_response(_call(asgi, _http(seed))) for seed in range(50)]
    for other in range(1000, 1100):
        _call(asgi, _http(other))
    assert [_response(_call(asgi, _http(seed))) for seed in range(50)] == first


def test_a_fault_ends_one_request_not_its_neighbours() -> None:
    """Concurrent requests: each gets the response it gets alone."""
    _, app = _writing_app()
    asgi = IndefiniteMiddleware(app)
    alone = {seed: _response(_call(asgi, _http(seed))) for seed in SEEDS}

    async def one(seed: int) -> tuple[int, str | None]:
        sent: list[Message] = []

        async def send(message: Message) -> None:
            await asyncio.sleep(0)  # interleave with the other requests
            sent.append(message)

        async def receive() -> Message:
            return {"type": "http.request"}

        await asgi(_http(seed), receive, send)
        return _response(sent)

    async def main() -> list[tuple[int, str | None]]:
        return await asyncio.gather(*(one(seed) for seed in SEEDS))

    assert asyncio.run(main()) == [alone[seed] for seed in SEEDS]


def test_a_fault_in_a_task_group_ends_the_request() -> None:
    """The TaskGroup wraps it in a BaseExceptionGroup; the middleware sees through it."""

    @indefinite(name="app.child")
    async def child() -> None:
        await asyncio.sleep(0)

    async def app(scope: Scope, receive: Receive, send: Send) -> None:
        async with asyncio.TaskGroup() as tg:
            tg.create_task(child())
        await send({"type": "http.response.start", "status": 200, "headers": []})
        await send({"type": "http.response.body", "body": b""})

    asgi = IndefiniteMiddleware(app)
    statuses = {_response(_call(asgi, _http(seed)))[0] for seed in SEEDS}
    assert statuses == {200, 500}


def test_too_late_to_answer_re_raises() -> None:
    """The response already started: the server must close the connection."""

    @indefinite(name="app.stream")
    async def chunk() -> bytes:
        return b"chunk"

    async def app(scope: Scope, receive: Receive, send: Send) -> None:
        await send({"type": "http.response.start", "status": 200, "headers": []})
        await send({"type": "http.response.body", "body": await chunk()})

    asgi = IndefiniteMiddleware(app)
    raised = 0
    for seed in SEEDS:
        sent: list[Message] = []

        async def send(message: Message, sent: list[Message] = sent) -> None:
            sent.append(message)

        async def receive() -> Message:
            return {"type": "http.request"}

        try:
            asyncio.run(asgi(_http(seed), receive, send))
        except _Abort:
            raised += 1
            assert [m["type"] for m in sent] == ["http.response.start"]
    assert raised > 0


def test_request_faults_write_the_line(capfd: pytest.CaptureFixture[str]) -> None:
    _, app = _writing_app()
    asgi = IndefiniteMiddleware(app)
    faults = [f for seed in SEEDS if (f := _response(_call(asgi, _http(seed)))[1])]
    assert faults
    assert capfd.readouterr().err == "".join(f"indefinite-error: {f}\n" for f in faults)


# --- What the middleware passes through untouched ------------------------------


def test_passes_through_without_a_seed() -> None:
    state, app = _writing_app()
    asgi = IndefiniteMiddleware(app)
    for _ in range(50):
        assert _response(_call(asgi, _http(None))) == (201, None)
    assert state == [1] * 50


def test_passes_through_non_http_scopes() -> None:
    seen: list[str] = []

    async def app(scope: Scope, receive: Receive, send: Send) -> None:
        seen.append(scope["type"])
        assert indefinite_error._active.get() is None  # noqa: SLF001

    asgi = IndefiniteMiddleware(app)
    for kind in ("lifespan", "websocket"):
        _call(asgi, {"type": kind, "headers": [(b"x-indefinite-seed", b"1")]})
    assert seen == ["lifespan", "websocket"]


def test_an_app_error_is_not_ours_to_answer() -> None:
    async def app(scope: Scope, receive: Receive, send: Send) -> None:
        raise DefiniteError

    with pytest.raises(DefiniteError):
        _call(IndefiniteMiddleware(app), _http(1))


SEED_HEADER_CASES = [
    (json.loads(values), None if outcome == "400" else int(outcome))
    for values, outcome in spec_rows("seed-header.tsv")
]


@pytest.mark.parametrize(("values", "outcome"), SEED_HEADER_CASES, ids=str)
def test_seed_header_matches_the_spec(values: list[str], outcome: int | None) -> None:
    """A request runs under the spec's seed, or gets a 400 and never reaches the app."""
    seeds: list[int] = []

    async def app(scope: Scope, receive: Receive, send: Send) -> None:
        injection = indefinite_error._active.get()  # noqa: SLF001
        assert injection is not None
        seeds.append(injection.seed)
        await send({"type": "http.response.start", "status": 200, "headers": []})
        await send({"type": "http.response.body", "body": b""})

    headers = [(b"x-indefinite-seed", v.encode()) for v in values]
    sent = _call(IndefiniteMiddleware(app), {"type": "http", "headers": headers})
    if outcome is None:
        assert (sent[0]["status"], seeds) == (400, [])
    else:
        assert (sent[0]["status"], seeds) == (200, [outcome])


@pytest.mark.parametrize("raw", [b"abc", b"\xff", b"1" * 65])
def test_malformed_seed_is_a_400(raw: bytes) -> None:
    """Bytes the spec's UTF-8 can't carry get a 400 too; the body says why."""
    state, app = _writing_app()
    sent = _call(IndefiniteMiddleware(app), _http(raw))
    assert sent == [
        {
            "type": "http.response.start",
            "status": 400,
            "headers": [(b"content-type", b"text/plain")],
        },
        {"type": "http.response.body", "body": b"X-Indefinite-Seed must be a decimal int64"},
    ]
    assert state == [], "the app never ran"


# --- Exception groups: whose outcome wins ----------------------------------------

FAULT = Fault(seed=7, site="app.x", n=0, phase="after")


def _raising(exc: BaseException) -> App:
    async def app(scope: Scope, receive: Receive, send: Send) -> None:
        raise exc

    return app


@pytest.mark.parametrize(
    "exc",
    [
        BaseExceptionGroup("tg", [_Abort(FAULT)]),
        BaseExceptionGroup("tg", [BaseExceptionGroup("inner", [_Abort(FAULT)])]),
        BaseExceptionGroup("tg", [ValueError("sibling"), _Abort(FAULT)]),
    ],
    ids=["alone", "nested", "beside-an-exception"],
)
def test_a_grouped_fault_ends_the_request(exc: BaseException) -> None:
    assert _response(_call(IndefiniteMiddleware(_raising(exc)), _http(1))) == (500, str(FAULT))


@pytest.mark.parametrize(
    "exc",
    [
        BaseExceptionGroup("tg", [KeyboardInterrupt(), _Abort(FAULT)]),
        ExceptionGroup("tg", [ValueError("no fault here")]),
    ],
    ids=["beside-a-teardown", "no-fault"],
)
def test_a_group_the_fault_does_not_own_propagates(exc: BaseException) -> None:
    with pytest.raises(BaseExceptionGroup) as info:
        _call(IndefiniteMiddleware(_raising(exc)), _http(1))
    assert info.value is exc


def test_installed_twice_is_a_loud_misconfiguration() -> None:
    _, app = _writing_app()
    with pytest.raises(RuntimeError) as info:
        _call(IndefiniteMiddleware(IndefiniteMiddleware(app)), _http(1))
    assert str(info.value) == (
        "a request seeded 1 inside one seeded 1: is IndefiniteMiddleware installed twice?"
    )
