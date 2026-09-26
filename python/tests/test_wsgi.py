"""The shipped WSGI middleware: one injection per request, and its table."""

from __future__ import annotations

import json
from concurrent.futures import ThreadPoolExecutor
from typing import TYPE_CHECKING, Any

import pytest

import indefinite_error
from indefinite_error import Fault, _Abort, indefinite
from indefinite_error.wsgi import IndefiniteMiddleware
from tests.helpers import SEEDS, DefiniteError, first_fault, spec_rows

if TYPE_CHECKING:
    from collections.abc import Callable, Iterable, MutableMapping

type Environ = MutableMapping[str, Any]
type StartResponse = Callable[..., Any]
type App = Callable[[Environ, StartResponse], Iterable[bytes]]


def _server() -> tuple[dict[str, Any], StartResponse]:
    """What a WSGI server hands an app: a recorder for headers and ``write``."""
    out: dict[str, Any] = {"write": []}

    def start_response(status: str, headers: list[tuple[str, str]], exc_info: Any = None) -> Any:
        out["status"] = status
        out["headers"] = headers
        out["exc_info"] = exc_info
        return out["write"].append

    return out, start_response


def _call(wsgi: App, environ: Environ) -> dict[str, Any]:
    """Run a WSGI request to completion and return what the server sent."""
    out, start_response = _server()
    out["body"] = b"".join(wsgi(environ, start_response))
    return out


def _http(seed: int | str | bytes | None) -> Environ:
    if seed is None:
        return {}
    # PEP 3333: a header's bytes become a native str via latin-1.
    value = seed.decode("latin-1") if isinstance(seed, bytes) else str(seed)
    return {"HTTP_X_INDEFINITE_SEED": value}


def _status(out: dict[str, Any]) -> int:
    return int(out["status"].split(" ", 1)[0])


def _fault(out: dict[str, Any]) -> str | None:
    return dict(out["headers"]).get("X-Indefinite-Fault")


def _result(wsgi: App, seed: int | str | bytes | None) -> tuple[int, str | None]:
    """One request's status and the fault its header names."""
    out = _call(wsgi, _http(seed))
    return _status(out), _fault(out)


def _writing_app() -> tuple[list[int], App]:
    state: list[int] = []

    @indefinite(name="app.write")
    def write(x: int) -> None:
        state.append(x)

    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        write(1)
        start_response("201 Created", [])
        return [b"created"]

    return state, app


# --- The table -----------------------------------------------


def test_rows_match_the_table() -> None:
    """No fault: the real response; before: 500, unchanged; after: 500, changed."""
    state, app = _writing_app()
    wsgi = IndefiniteMiddleware(app)
    rows = set()
    for seed in SEEDS:
        state.clear()
        out = _call(wsgi, _http(seed))
        fault = _fault(out)
        phase = None if fault is None else fault.split(" ", 1)[0]
        rows.add((_status(out), phase, state == [1]))
        if fault is not None:
            assert fault.endswith(f"(seed={seed})")
    assert rows == {(201, None, True), (500, "before", False), (500, "after", True)}


def test_the_fault_response_has_no_body() -> None:
    _, app = _writing_app()
    wsgi = IndefiniteMiddleware(app)
    for seed in SEEDS:
        out = _call(wsgi, _http(seed))
        if _status(out) == 500:
            assert out["status"] == "500 Internal Server Error"
            assert out["body"] == b""
            assert out["headers"] == [("X-Indefinite-Fault", _fault(out))]
            return
    pytest.fail("no seed faulted")


def test_the_seed_is_a_whole_request_input() -> None:
    """The same seed takes the same path at request 1 or request 100."""
    _, app = _writing_app()
    wsgi = IndefiniteMiddleware(app)
    first = [_result(wsgi, s) for s in range(50)]
    for other in range(1000, 1100):
        _call(wsgi, _http(other))
    assert [_result(wsgi, s) for s in range(50)] == first


def test_a_fault_ends_one_request_not_its_neighbours() -> None:
    """Concurrent requests in their own threads: each gets what it gets alone."""
    _, app = _writing_app()
    wsgi = IndefiniteMiddleware(app)
    alone = {s: _result(wsgi, s) for s in SEEDS}
    with ThreadPoolExecutor(max_workers=8) as pool:
        got = list(pool.map(lambda s: _result(wsgi, s), SEEDS))
    assert got == [alone[s] for s in SEEDS]


def test_request_faults_write_the_line(capfd: pytest.CaptureFixture[str]) -> None:
    _, app = _writing_app()
    wsgi = IndefiniteMiddleware(app)
    faults = [f for s in SEEDS if (f := _fault(_call(wsgi, _http(s))))]
    assert faults
    assert capfd.readouterr().err == "".join(f"indefinite-error: {f}\n" for f in faults)


# --- Committing the response --------------------------------------------------


def test_an_empty_body_still_sends_its_headers() -> None:
    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        start_response("204 No Content", [])
        return []

    out = _call(IndefiniteMiddleware(app), _http(1))
    assert (_status(out), out["body"]) == (204, b"")


def test_a_write_callable_commits_the_response() -> None:
    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        write = start_response("200 OK", [])
        write(b"hi")
        return []

    out = _call(IndefiniteMiddleware(app), _http(1))
    assert (_status(out), out["write"], out["body"]) == (200, [b"hi"], b"")


def test_a_fault_after_start_response_but_before_the_body_answers_500() -> None:
    """Nothing has committed yet, so the fault can still answer."""

    @indefinite(name="app.early")
    def early() -> None:
        return None

    fault = first_fault(early)

    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        start_response("200 OK", [])
        early()
        return [b"body"]  # pragma: no cover - the fault unwinds first

    out = _call(IndefiniteMiddleware(app), _http(fault.seed))
    assert (_status(out), _fault(out), out["body"]) == (500, str(fault), b"")


def test_too_late_to_answer_re_raises() -> None:
    """A fault after the body committed: the server must close the connection."""

    @indefinite(name="app.chunk")
    def chunk() -> bytes:
        return b"chunk"

    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        start_response("200 OK", [])
        yield b"first"
        yield chunk()

    wsgi = IndefiniteMiddleware(app)
    raised = 0
    for seed in SEEDS:
        out, start_response = _server()
        try:
            b"".join(wsgi(_http(seed), start_response))
        except _Abort:
            raised += 1
            assert out["status"] == "200 OK"  # committed before the fault
    assert raised > 0


def test_a_fault_after_write_re_raises() -> None:
    @indefinite(name="app.late")
    def late() -> None:
        return None

    fault = first_fault(late)

    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        write = start_response("200 OK", [])
        write(b"hi")
        late()
        return []  # pragma: no cover - the fault unwinds first

    out, start_response = _server()
    with pytest.raises(_Abort):
        b"".join(IndefiniteMiddleware(app)(_http(fault.seed), start_response))
    assert out["write"] == [b"hi"]


def test_the_body_is_closed_after_a_successful_response() -> None:
    class Body:
        def __init__(self) -> None:
            self.closed = False

        def __iter__(self) -> Any:
            return iter([b"x"])

        def close(self) -> None:
            self.closed = True

    bodies: list[Body] = []

    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        start_response("200 OK", [])
        body = Body()
        bodies.append(body)
        return body

    _call(IndefiniteMiddleware(app), _http(1))
    assert bodies[0].closed


def test_the_body_is_closed_when_a_fault_abandons_it() -> None:
    @indefinite(name="app.boom")
    def boom() -> None:
        return None

    fault = first_fault(boom)
    bodies: list[Any] = []

    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        class Body:
            def __init__(self) -> None:
                self.closed = False

            def __iter__(self) -> Any:
                start_response("200 OK", [])
                boom()
                return iter([b""])

            def close(self) -> None:
                self.closed = True

        body = Body()
        bodies.append(body)
        return body

    out = _call(IndefiniteMiddleware(app), _http(fault.seed))
    assert (_status(out), bodies[0].closed) == (500, True)


def test_a_body_before_start_response_is_a_loud_protocol_violation() -> None:
    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        return [b"body"]  # never called start_response

    with pytest.raises(AssertionError) as info:
        _call(IndefiniteMiddleware(app), _http(1))
    assert str(info.value) == "the app produced a body before calling start_response"


def test_start_response_exc_info_is_forwarded() -> None:
    """The app's exc_info reaches the server, it isn't replaced with None."""
    info = (ValueError, ValueError("x"), None)

    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        start_response("200 OK", [], info)
        return []

    out = _call(IndefiniteMiddleware(app), _http(1))
    assert (out["status"], out["exc_info"]) == ("200 OK", info)


# --- What the middleware passes through untouched ------------------------------


def test_passes_through_without_a_seed() -> None:
    state, app = _writing_app()
    wsgi = IndefiniteMiddleware(app)
    for _ in range(50):
        assert _status(_call(wsgi, _http(None))) == 201
    assert state == [1] * 50


def test_passes_through_without_opening_an_injection() -> None:
    seen: list[object] = []

    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        seen.append(indefinite_error._active.get())  # noqa: SLF001
        start_response("200 OK", [])
        return [b""]

    _call(IndefiniteMiddleware(app), {})
    assert seen == [None]


def test_an_app_error_is_not_ours_to_answer() -> None:
    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        raise DefiniteError

    with pytest.raises(DefiniteError):
        _call(IndefiniteMiddleware(app), _http(1))


SEED_HEADER_CASES = [
    (json.loads(values), None if outcome == "400" else int(outcome))
    for values, outcome in spec_rows("seed-header.tsv")
]


def _case_id(values: list[str]) -> str:
    """An ASCII node id: the Arabic-Indic digit in the spec can't be one."""
    parts = (
        v.encode("unicode_escape").decode("ascii").replace(" ", "_") or "empty" for v in values
    )
    return "-".join(parts)


@pytest.mark.parametrize(
    ("values", "outcome"), SEED_HEADER_CASES, ids=[_case_id(v) for v, _ in SEED_HEADER_CASES]
)
def test_seed_header_matches_the_spec(values: list[str], outcome: int | None) -> None:
    """A request runs under the spec's seed, or gets a 400 and never reaches the app."""
    seeds: list[int] = []

    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
        injection = indefinite_error._active.get()  # noqa: SLF001
        assert injection is not None
        seeds.append(injection.seed)
        start_response("200 OK", [])
        return [b""]

    # A WSGI server folds a repeated header into one comma-separated value.
    out = _call(IndefiniteMiddleware(app), {"HTTP_X_INDEFINITE_SEED": ",".join(values)})
    if outcome is None:
        assert (_status(out), seeds) == (400, [])
    else:
        assert (_status(out), seeds) == (200, [outcome])


@pytest.mark.parametrize("raw", [b"abc", b"\xff", b"1" * 65])
def test_malformed_seed_is_a_400(raw: bytes) -> None:
    """Bytes the spec's UTF-8 can't carry get a 400 too; the body says why."""
    state, app = _writing_app()
    out = _call(IndefiniteMiddleware(app), _http(raw))
    assert (_status(out), out["headers"], out["body"]) == (
        400,
        [("content-type", "text/plain")],
        b"X-Indefinite-Seed must be a decimal int64",
    )
    assert state == [], "the app never ran"


# --- Exception groups: whose outcome wins ----------------------------------------

FAULT = Fault(seed=7, site="app.x", n=0, phase="after")


def _raising(exc: BaseException) -> App:
    def app(environ: Environ, start_response: StartResponse) -> Iterable[bytes]:
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
    out = _call(IndefiniteMiddleware(_raising(exc)), _http(1))
    assert (_status(out), _fault(out)) == (500, str(FAULT))


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
