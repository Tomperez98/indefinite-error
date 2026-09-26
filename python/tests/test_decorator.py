"""What ``@indefinite`` accepts, what it rejects, and how it names a site."""

from __future__ import annotations

import asyncio
import contextlib
import functools
import inspect
from typing import TYPE_CHECKING, cast

import pytest

import indefinite_error
import indefinite_error.asgi
from indefinite_error import _Abort, _inject, indefinite
from tests.helpers import SEEDS, attempt, first_fault, seed_where

if TYPE_CHECKING:
    from collections.abc import AsyncIterator, Callable, Generator, Iterator


def test_public_api() -> None:
    """Two names: mark a site, and install the one thing that catches its faults."""
    assert indefinite_error.__all__ == ["indefinite"]
    assert indefinite_error.asgi.__all__ == ["IndefiniteMiddleware"]


# --- Rejected at decoration time ----------------------------------------------


def _gen() -> Iterator[int]:
    yield 1


async def _agen() -> AsyncIterator[int]:
    yield 1


class _GenCall:
    def __call__(self) -> Iterator[int]:
        yield 1


@pytest.mark.parametrize(
    "fn",
    [_gen, _agen, functools.partial(_gen), _GenCall()],
    ids=["generator", "async-generator", "partial-of-generator", "generator-__call__"],
)
def test_generators_rejected(fn: Callable[[], object]) -> None:
    with pytest.raises(TypeError, match="does not support generators"):
        indefinite(name="gen")(fn)


def test_class_rejected() -> None:
    class C:
        pass

    for decorate in (indefinite, indefinite(name="c")):
        with pytest.raises(TypeError, match="needs a function"):
            decorate(C)


def test_non_callable_rejected() -> None:
    with pytest.raises(TypeError, match="needs a function"):
        indefinite(name="x")(cast("Callable[[], None]", 42))


def test_unnamed_callable_object_hints_at_name() -> None:
    class Op:
        def __call__(self) -> None:
            pass

    for fn in (Op(), functools.partial(print)):
        with pytest.raises(TypeError, match="pass name= to wrap it"):
            indefinite(fn)


def test_lambda_needs_a_name() -> None:
    with pytest.raises(TypeError, match="lambda needs name="):
        indefinite(lambda: None)
    assert indefinite(name="noop")(lambda: 1)() == 1


@pytest.mark.parametrize(
    "descriptor",
    [
        classmethod(lambda _cls: None),
        staticmethod(lambda: None),
        property(lambda _self: None),
        functools.cached_property(lambda _self: None),
    ],
    ids=["classmethod", "staticmethod", "property", "cached_property"],
)
def test_descriptor_above_indefinite_rejected(descriptor: object) -> None:
    """``inspect.isroutine`` accepts some of these, but wrapping them breaks them."""
    with pytest.raises(TypeError, match="innermost"):
        indefinite(cast("Callable[[], None]", descriptor))


@pytest.mark.parametrize("bad", ["", 1, False])
def test_name_must_be_nonempty_str(bad: object) -> None:
    with pytest.raises(TypeError, match="name must be a non-empty str"):
        indefinite(name=cast("str", bad))


@pytest.mark.parametrize("bad", ["db commit", "db.commit\n", "tab\there", "nul\x00", "\u2028"])
def test_name_cannot_break_the_fault_line(bad: str) -> None:
    """The site goes on one stderr line; whitespace or control chars would garble it."""
    with pytest.raises(ValueError, match="printable with no whitespace"):
        indefinite(name=bad)


def test_default_site_cannot_break_the_fault_line() -> None:
    def op() -> None:
        pass

    op.__qualname__ = "has space"
    with pytest.raises(ValueError, match="printable with no whitespace"):
        indefinite(op)


def test_bare_call_rejected() -> None:
    with pytest.raises(TypeError, match="needs a function or a name"):
        indefinite()


# --- Accepted: descriptors below it, partials, callable objects, methods ------


def test_descriptors_below_indefinite_work() -> None:
    class C:
        @classmethod
        @indefinite
        def cm(cls) -> str:
            return "cm"

        @staticmethod
        @indefinite
        def sm() -> str:
            return "sm"

        @property
        @indefinite
        def x(self) -> str:
            return "x"

    assert (C.cm(), C.sm(), C().x) == ("cm", "sm", "x")
    with _inject(0) as inj:
        for call in (C.cm, C.sm, lambda: C().x):
            with contextlib.suppress(_Abort):
                call()
    prefix = f"{__name__}.test_descriptors_below_indefinite_work.<locals>.C"
    assert inj.calls == {f"{prefix}.cm": 1, f"{prefix}.sm": 1, f"{prefix}.x": 1}


def test_method_on_two_instances_shares_a_site() -> None:
    class Store:
        @indefinite
        def put(self, i: int) -> int:
            return i * 2

    a, b = Store(), Store()
    with _inject(0) as inj:
        attempt(a.put, 0)
        attempt(b.put, 0)
    assert list(inj.calls.values()) == [2]


def test_bound_method_with_a_name() -> None:
    """The README's ``indefinite(name="db.commit")(session.commit)``."""
    committed: list[int] = []

    class Session:
        def commit(self, i: int) -> int:
            committed.append(i)
            return i * 2

    commit = indefinite(name="db.commit")(Session().commit)
    assert first_fault(lambda: commit(0)).site == "db.commit"


def test_named_partial_and_callable_object() -> None:
    ran: list[int] = []

    def mul(a: int, b: int) -> int:
        ran.append(b)
        return a * b

    class Double:
        def __call__(self, i: int) -> int:
            ran.append(i)
            return i * 2

    ops = {
        "double": indefinite(name="double")(Double()),
        "mul": indefinite(name="mul")(functools.partial(mul, 2)),
    }
    for site, op in ops.items():
        for seed in SEEDS:
            ran.clear()
            with _inject(seed) as inj:
                outcomes = [attempt(op, i) for i in range(50)]
            assert ran == [i for i, o in enumerate(outcomes) if o != "before"]
            assert set(inj.calls) == {site}


def test_callable_object_dict_not_copied() -> None:
    class Op:
        def __init__(self) -> None:
            self.secret = 1

        def __call__(self) -> None:
            pass

    assert not hasattr(indefinite(name="op")(Op()), "secret")


# --- Async detection: what runs is what counts ---------------------------------


class _AsyncDouble:
    def __init__(self) -> None:
        self.ran: list[int] = []

    async def __call__(self, i: int) -> int:
        self.ran.append(i)
        return i * 2


async def _async_double(ran: list[int], i: int) -> int:
    ran.append(i)
    return i * 2


def test_async_callables_get_the_async_wrapper() -> None:
    obj = _AsyncDouble()
    ran: list[int] = []
    ops = [
        indefinite(name="obj")(obj),
        indefinite(name="partial")(functools.partial(_async_double, ran)),
    ]
    for op, record in zip(ops, [obj.ran, ran], strict=True):
        assert inspect.iscoroutinefunction(op)
        for seed in SEEDS:
            record.clear()
            outcomes = asyncio.run(_run_async(seed, op))
            assert record == [i for i, o in enumerate(outcomes) if o != "before"]


async def _run_async(seed: int, op: Callable[[int], object]) -> list[str]:
    with _inject(seed):
        out = []
        for i in range(50):
            try:
                assert await cast("asyncio.Future[int]", op(i)) == i * 2
                out.append("ok")
            except _Abort as e:
                out.append(e.fault.phase)
        return out


class _Awaitable:
    """Awaitable but not a coroutine: nothing to close."""

    def __await__(self) -> Generator[None, None, int]:
        yield
        return 1


@pytest.mark.parametrize(
    "make", [lambda: asyncio.sleep(0), _Awaitable], ids=["coroutine", "awaitable"]
)
def test_sync_returning_an_awaitable_panics(make: Callable[[], object]) -> None:
    """The outcome isn't known when the call returns, so AFTER would lie."""
    made: list[object] = []

    @indefinite
    def looks_sync() -> object:
        made.append(aw := make())
        return aw

    seen = set()
    for seed in SEEDS:
        with _inject(seed):
            try:
                looks_sync()
            except _Abort as e:
                seen.add(e.fault.phase)  # BEFORE never calls it: nothing to detect
            except TypeError as e:
                seen.add("panic" if "async def" in str(e) else repr(e))
            else:
                seen.add("returned an awaitable")
    assert seen == {"before", "panic"}
    assert made, "it was called"


def test_sync_returning_coroutine_panics_on_the_pass_path() -> None:
    """Even when no fault fires: the bug is in the op, not in the seed."""

    @indefinite(name="looks_sync")
    def looks_sync() -> object:
        return asyncio.sleep(0)

    with (
        _inject(seed_where("looks_sync", "off")),
        pytest.raises(TypeError, match="async def"),
    ):
        looks_sync()


def test_sync_returning_coroutine_is_left_alone_outside_inject() -> None:
    @indefinite
    def looks_sync() -> object:
        return asyncio.sleep(0, result=1)

    async def main() -> object:
        return await cast("asyncio.Future[object]", looks_sync())

    assert asyncio.run(main()) == 1


def test_positional_function_with_a_name() -> None:
    """``indefinite(fn, name=...)``: the name wins over module.qualname."""
    op = indefinite(lambda i: i * 2, name="positional")
    assert first_fault(lambda: op(0)).site == "positional"


def _module_none() -> None:
    pass


_module_none.__module__ = None  # ty: ignore[invalid-assignment]


class _Op:
    def __call__(self) -> None:
        pass

    def __repr__(self) -> str:
        return "<Op>"


def _named_object() -> Callable[[], None]:
    obj = _Op()
    obj.__qualname__ = "_Op"  # ty: ignore[unresolved-attribute]
    return obj


@pytest.mark.parametrize(
    "fn",
    [
        _module_none,
        object().__str__,  # a method-wrapper: a routine with no __module__
        _named_object(),  # not a routine, even with a str __module__ and __qualname__
    ],
    ids=["module-is-None", "no-__module__", "object-with-a-qualname"],
)
def test_default_site_needs_a_real_module_qualname(fn: Callable[..., object]) -> None:
    with pytest.raises(TypeError, match=r"needs a function or method, .*pass name="):
        indefinite(fn)


# --- The messages are the UX: pin them exactly -----------------------------------


def _misuse_awaitable() -> None:
    op = indefinite(name="looks_sync")(lambda: asyncio.sleep(0))
    with _inject(seed_where("looks_sync", "off")):
        pytest.fail(f"returned {op()!r} instead of panicking")


def _misuse_stacked() -> None:
    indefinite(indefinite(name="db.commit")(print))


MISUSE: dict[str, tuple[Callable[[], object], type[Exception], str]] = {
    "bare-call": (indefinite, TypeError, "@indefinite needs a function or a name"),
    "empty-name": (lambda: indefinite(name=""), TypeError, "name must be a non-empty str, got ''"),
    "unprintable-name": (
        lambda: indefinite(name="db commit"),
        ValueError,
        "site name must be printable with no whitespace, got 'db commit'",
    ),
    "descriptor": (
        lambda: indefinite(cast("Callable[[], None]", staticmethod(print))),
        TypeError,
        (
            "@indefinite must be the innermost decorator: apply it below "
            "@classmethod/@staticmethod/@property, not above staticmethod"
        ),
    ),
    "not-callable": (
        lambda: indefinite(name="x")(cast("Callable[[], None]", 42)),
        TypeError,
        "@indefinite needs a function or method, got 42",
    ),
    "unnamed-object": (
        lambda: indefinite(_Op()),
        TypeError,
        "@indefinite needs a function or method, got <Op>; pass name= to wrap it",
    ),
    "lambda": (
        lambda: indefinite(lambda: None),
        TypeError,
        (
            f"@indefinite on a lambda needs name=: every lambda in {__name__} "
            "would share one site and one fault stream"
        ),
    ),
    "generator": (
        lambda: indefinite(_gen),
        TypeError,
        f"@indefinite does not support generators: {__name__}._gen",
    ),
    "stacked": (_misuse_stacked, TypeError, "@indefinite is already applied to db.commit"),
    "sync-returns-awaitable": (
        _misuse_awaitable,
        TypeError,
        (
            "@indefinite: looks_sync is sync but returned coroutine, so the operation "
            "hasn't happened when the call returns. Make it `async def`, or mark it "
            "with inspect.markcoroutinefunction"
        ),
    ),
}


@pytest.mark.parametrize("case", MISUSE)
def test_misuse_message(case: str) -> None:
    misuse, error, message = MISUSE[case]
    with pytest.raises(error) as info:
        misuse()
    assert str(info.value) == message
