"""Reproducible injection of indefinite errors into the requests of a server.

    @indefinite
    def commit(tx) -> None: ...

    app = indefinite_error.asgi.IndefiniteMiddleware(app)

Outside a request carrying a seed, the decorator does nothing. Inside, a call
may fault BEFORE the function runs (it never happened) or AFTER it returns or
raises (it happened; nobody was told). A call that isn't faulted returns its
real value or raises its real exception. A teardown the function raises -- a
``BaseException`` that isn't an ``Exception``, such as ``KeyboardInterrupt``
or ``asyncio.CancelledError`` -- outranks the fault and propagates unchanged.

A fault writes one line to stderr, then ends its request: a private exception
unwinds it to the middleware, the only code that catches it, which answers
for it. Cleanup runs, as it would when a request is cancelled; the server and
its other requests carry on.

A site is named ``module.qualname`` by default, so two objects produced by the
same factory -- or the same method on two instances -- share one fault stream.
Pass ``name=`` to ``@indefinite`` to give each its own stable identity; it is
required for lambdas and for callables that aren't functions.

Every decision is derived from the request's seed alone: the same seed replays
the same faults.
"""

from __future__ import annotations

import contextlib
import functools
import hashlib
import inspect
import os
import threading
from contextvars import ContextVar
from dataclasses import dataclass
from typing import TYPE_CHECKING, Any, Literal, NoReturn, overload

if TYPE_CHECKING:
    from collections.abc import Callable, Iterator

__all__ = ["indefinite"]

type Phase = Literal["before", "after"]
type Mode = Literal["off", "before", "after", "both"]


@dataclass(frozen=True, slots=True)
class Fault:
    """One injected fault: the ``n``-th call to ``site`` in the request seeded ``seed``.

    Its ``str`` is the fault line's payload: ``after app.get#4 (seed=13)``.
    """

    seed: int
    site: str
    n: int
    phase: Phase

    def __str__(self) -> str:
        return f"{self.phase} {self.site}#{self.n} (seed={self.seed})"


class _Abort(BaseException):
    """Ends one request. Private: only ``indefinite_error.asgi`` catches it.

    A ``BaseException``, so ``except Exception`` and retry loops can't see it.
    Code that catches ``BaseException`` and doesn't re-raise swallows it, as it
    would swallow a cancellation.
    """

    def __init__(self, fault: Fault) -> None:
        super().__init__(str(fault))
        self.fault = fault


def _report(fault: Fault) -> bytes:
    """The one line every fault writes to stderr. Site names can't break it."""
    return f"indefinite-error: {fault}\n".encode()


def _abort(fault: Fault) -> NoReturn:
    """Write the fault line, then end the request.

    ``os.write`` on fd 2, not ``sys.stderr``: one unbuffered write, so lines
    from concurrent requests don't interleave or wait on a flush.
    """
    with contextlib.suppress(OSError):  # stderr closed: the response still says it
        os.write(2, _report(fault))
    raise _Abort(fault) from None  # not caused by what the caller was handling


# --- Pure core: every decision is a function of (seed, site, n) -------------

# Per-seed fault rates (swarm testing): some seeds are gentle, some brutal.
_RATES: tuple[float, ...] = (0.01, 0.05, 0.2, 0.5)
_MODES: tuple[Mode, ...] = ("off", "before", "after", "both")


def _unit(*parts: str | int) -> float:
    """Uniform float in [0, 1) from ``parts``, stable across processes.

    Uses blake2b, not ``hash()``: string hashing is salted per process.
    """
    data = "\x00".join(str(p) for p in parts).encode()
    digest = hashlib.blake2b(data, digest_size=8).digest()
    return int.from_bytes(digest) / 2**64  # big-endian


def _pick[T](options: tuple[T, ...], u: float) -> T:
    assert 0.0 <= u < 1.0, f"u={u} out of [0, 1)"
    return options[int(u * len(options))]


def _rate(seed: int) -> float:
    return _pick(_RATES, _unit("rate", seed))


def _mode(seed: int, site: str) -> Mode:
    return _pick(_MODES, _unit("mode", seed, site))


def _decide(seed: int, rate: float, mode: Mode, site: str, n: int) -> Phase | None:
    """The phase of the ``n``-th call to ``site``; ``rate`` and ``mode`` derive from seed."""
    if mode == "off" or _unit("call", seed, site, n) >= rate:
        return None
    if mode == "both":
        # Not mutated: `<= 0.5` would differ only on a draw of exactly 0.5.
        return "before" if _unit("phase", seed, site, n) < 0.5 else "after"  # pragma: no mutate
    return mode


# --- Injection: the only state is a call counter per site -------------------


class Injection:
    """One request's injection: what its seed chose, and which sites it reached.

    Opened by ``_inject()``, for the middleware. Everything is read-only, so
    nothing can shift the replay. Once ``_inject()`` exits, it is closed: a task
    that outlives the request calls through with no faults.
    """

    __slots__ = ("_calls", "_closed", "_lock", "_modes", "_rate", "_seed")

    def __init__(self, seed: int) -> None:
        self._seed = seed
        self._rate = _rate(seed)
        self._calls: dict[str, int] = {}
        self._modes: dict[str, Mode] = {}
        self._closed = False  # pragma: no mutate (None is just as falsy)
        self._lock = threading.Lock()

    @property
    def seed(self) -> int:
        return self._seed

    @property
    def rate(self) -> float:
        """The chance that a call to a site whose mode isn't ``off`` faults."""
        return self._rate

    @property
    def calls(self) -> dict[str, int]:
        """Calls per site so far (a copy). A missing site was never reached."""
        with self._lock:
            return dict(self._calls)

    @property
    def modes(self) -> dict[str, Mode]:
        """Which phases may fault, per site reached so far (a copy)."""
        with self._lock:
            return dict(self._modes)

    @property
    def closed(self) -> bool:
        """Whether ``_inject()`` has exited; a closed injection injects nothing."""
        with self._lock:
            return self._closed

    def _next(self, site: str) -> Fault | None:
        with self._lock:
            if self._closed:
                return None
            n = self._calls.get(site, 0)
            self._calls[site] = n + 1
            mode = self._modes.get(site)  # pragma: no mutate (a cache: a miss recomputes)
            if mode is None:
                mode = self._modes[site] = _mode(self._seed, site)
            phase = _decide(self._seed, self._rate, mode, site, n)
            return None if phase is None else Fault(self._seed, site, n, phase)

    def _close(self) -> None:
        with self._lock:
            self._closed = True

    def __repr__(self) -> str:
        with self._lock:
            sites = ", ".join(f"{s}={self._modes[s]}" for s in sorted(self._modes))
        return f"Injection(seed={self._seed}, rate={self._rate}, sites=[{sites}])"


_active: ContextVar[Injection | None] = ContextVar("indefinite_error", default=None)


@contextlib.contextmanager
def _inject(seed: int) -> Iterator[Injection]:
    """Inject faults into ``@indefinite`` calls for one request, driven by ``seed``.

    Private: a fault raises ``_Abort``, and only the middleware catches it. The
    injection follows the current context: asyncio tasks created inside and
    ``asyncio.to_thread`` see it; a plain thread or executor only if its work
    runs inside ``contextvars.copy_context().run``.
    """
    assert isinstance(seed, int) and not isinstance(seed, bool), f"seed {seed!r}"
    active = _active.get()
    if active is not None and not active.closed:
        msg = (
            f"a request seeded {seed} inside one seeded {active.seed}: "
            "is IndefiniteMiddleware installed twice?"
        )
        raise RuntimeError(msg)
    injection = Injection(seed)
    token = _active.set(injection)
    try:
        yield injection
    finally:
        injection._close()  # noqa: SLF001 - _inject() owns the injection's lifecycle
        _active.reset(token)


# --- Decorator ---------------------------------------------------------------

# Descriptors that `inspect.isroutine` may accept but that break when wrapped.
_DESCRIPTORS = (classmethod, staticmethod, property, functools.cached_property)


@overload
def indefinite[**P, R](fn: Callable[P, R], /, *, name: str | None = None) -> Callable[P, R]: ...


@overload
def indefinite(fn: None = None, /) -> NoReturn: ...


@overload
def indefinite[**P, R](
    fn: None = None, /, *, name: str
) -> Callable[[Callable[P, R]], Callable[P, R]]: ...


def indefinite[**P, R](fn: Callable[P, R] | None = None, /, *, name: str | None = None) -> Any:
    """Mark ``fn`` as an operation whose outcome can be indefinite.

    Use bare (``@indefinite``) to name the site ``module.qualname``, or
    ``@indefinite(name="...")`` to override it -- useful when a factory or a
    per-instance method would otherwise share one site, and therefore one
    fault stream, with its siblings. ``name=`` is required for lambdas and for
    callables that aren't functions (``functools.partial``, objects with
    ``__call__``).
    """
    if name is not None and (not isinstance(name, str) or not name):
        msg = f"name must be a non-empty str, got {name!r}"
        raise TypeError(msg)
    if name is not None:
        _check_site(name)
    if fn is None:
        if name is None:
            msg = "@indefinite needs a function or a name"
            raise TypeError(msg)
        return _named(name)
    return _decorate(fn, name)


def _named[**P, R](name: str) -> Callable[[Callable[P, R]], Callable[P, R]]:
    def decorate(fn: Callable[P, R], /) -> Callable[P, R]:
        return _decorate(fn, name)

    return decorate


def _check_site(site: str) -> None:
    """Panic on a site that would make the fault line ambiguous to parse."""
    if not site.isprintable() or any(c.isspace() for c in site):
        msg = f"site name must be printable with no whitespace, got {site!r}"
        raise ValueError(msg)


def _default_site(fn: object) -> str:
    """``module.qualname`` for a function or method; panics for anything else."""
    qualname = getattr(fn, "__qualname__", None)
    module = getattr(fn, "__module__", None)
    if not inspect.isroutine(fn) or not isinstance(qualname, str) or not isinstance(module, str):
        msg = f"@indefinite needs a function or method, got {fn!r}; pass name= to wrap it"
        raise TypeError(msg)
    # Not mutated: a routine with a str module and qualname always has a __name__.
    if getattr(fn, "__name__", None) == "<lambda>":  # pragma: no mutate
        msg = (
            f"@indefinite on a lambda needs name=: every lambda in {module} "
            "would share one site and one fault stream"
        )
        raise TypeError(msg)
    site = f"{module}.{qualname}"
    _check_site(site)
    return site


def _not_awaitable[R](site: str, result: R) -> R:
    """Panic if a sync call returned an awaitable: its outcome isn't known yet."""
    if inspect.isawaitable(result):
        if inspect.iscoroutine(result):
            result.close()  # it will never run; don't also warn "never awaited"
        msg = (
            f"@indefinite: {site} is sync but returned {type(result).__name__}, "
            "so the operation hasn't happened when the call returns. "
            "Make it `async def`, or mark it with inspect.markcoroutinefunction"
        )
        raise TypeError(msg)
    return result


def _decorate[**P, R](fn: Callable[P, R], name: str | None) -> Callable[P, R]:
    if isinstance(fn, _DESCRIPTORS):
        msg = (
            "@indefinite must be the innermost decorator: apply it below "
            f"@classmethod/@staticmethod/@property, not above {type(fn).__name__}"
        )
        raise TypeError(msg)
    if inspect.isclass(fn) or not callable(fn):
        msg = f"@indefinite needs a function or method, got {fn!r}"
        raise TypeError(msg)
    applied = getattr(fn, "__indefinite_site__", None)
    if applied is not None:
        msg = f"@indefinite is already applied to {applied}"
        raise TypeError(msg)
    site = name if name is not None else _default_site(fn)

    # What actually runs: a partial is unwrapped by `inspect`; an object, its __call__.
    is_function = inspect.isroutine(fn) or isinstance(fn, functools.partial)
    target = fn if is_function else type(fn).__call__
    if inspect.isgeneratorfunction(target) or inspect.isasyncgenfunction(target):
        msg = f"@indefinite does not support generators: {site}"
        raise TypeError(msg)
    # Don't copy an object's instance __dict__ onto the wrapper function.
    wraps = functools.wraps(fn, updated=functools.WRAPPER_UPDATES if is_function else ())

    if inspect.iscoroutinefunction(target):

        @wraps
        async def async_wrapper(*args: P.args, **kwargs: P.kwargs) -> Any:
            injection = _active.get()
            fault = None if injection is None else injection._next(site)  # noqa: SLF001
            if fault is None:
                return await fn(*args, **kwargs)  # ty: ignore[invalid-await]
            if fault.phase == "before":
                _abort(fault)
            try:
                await fn(*args, **kwargs)  # ty: ignore[invalid-await]
            except Exception:
                _abort(fault)  # it ran and raised; a teardown propagates instead
            _abort(fault)

        async_wrapper.__indefinite_site__ = site  # ty: ignore[unresolved-attribute]
        return async_wrapper  # ty: ignore[invalid-return-type]

    @wraps
    def wrapper(*args: P.args, **kwargs: P.kwargs) -> R:
        injection = _active.get()
        if injection is None:
            return fn(*args, **kwargs)
        fault = injection._next(site)  # noqa: SLF001
        if fault is None:
            return _not_awaitable(site, fn(*args, **kwargs))
        if fault.phase == "before":
            _abort(fault)
        try:
            result = fn(*args, **kwargs)
        except Exception:
            _abort(fault)  # it ran and raised; a teardown propagates instead
        # A sync call that returned an awaitable hasn't happened yet: AFTER would lie.
        _not_awaitable(site, result)
        _abort(fault)

    wrapper.__indefinite_site__ = site  # ty: ignore[unresolved-attribute]
    return wrapper
