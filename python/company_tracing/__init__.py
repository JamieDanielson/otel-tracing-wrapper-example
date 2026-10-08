"""Shared tracing helpers for internal services.

Two decorators cover the cases auto-instrumentation misses:

* ``traced`` wraps any sync or async function in a span that is a child of
  whatever span is already active (HTTP request, celery task, parent job).
* ``job_run`` wraps one execution of a polling/scheduled job in a fresh
  root span, so every run is its own trace instead of one unbounded span.

Both work with ``opentelemetry-instrument`` (zero-code) and with a manually
configured SDK, because the tracer is resolved through the global API and is
a no-op until a provider is installed.
"""

from __future__ import annotations

import functools
import inspect
import time
from collections.abc import Callable, Iterator, Mapping, Sequence
from contextlib import contextmanager
from typing import Any, ParamSpec, TypeVar

from opentelemetry import context, trace
from opentelemetry.trace import Span, SpanKind, Status, StatusCode

P = ParamSpec("P")
R = TypeVar("R")

# Resolved once per process. Before an SDK is configured this is a
# ProxyTracer that becomes real once a TracerProvider is set, so import
# order between this module and SDK setup does not matter.
_tracer = trace.get_tracer("company_tracing", "0.1.0")

# Custom attributes live under a company-owned prefix. ``code.*`` is a
# semantic-conventions namespace and may gain keys that collide with ours.
ARG_ATTRIBUTE_PREFIX = "app.arg."


def _qualified_name(func: Callable[..., Any]) -> str:
    return f"{func.__module__}.{func.__qualname__}"


def _reject_generators(func: Callable[..., Any]) -> None:
    # A span around a generator function covers only the call that creates
    # the generator object, not the iteration, so it would be misleading.
    if inspect.isgeneratorfunction(func) or inspect.isasyncgenfunction(func):
        raise TypeError(
            f"{_qualified_name(func)} is a generator function; "
            "wrap the consuming code instead."
        )


def _arg_attribute_binder(
    func: Callable[..., Any], record_args: Sequence[str]
) -> Callable[[tuple[Any, ...], dict[str, Any]], dict[str, Any]]:
    """Build a function that extracts the named parameters of a call as attributes.

    The signature is inspected once here rather than on every call.
    """
    if isinstance(record_args, str):
        raise TypeError("record_args must be a sequence of names, not a string")
    wanted = frozenset(record_args)
    if not wanted:
        return lambda args, kwargs: {}

    signature = inspect.signature(func)

    def bind(args: tuple[Any, ...], kwargs: dict[str, Any]) -> dict[str, Any]:
        try:
            bound = signature.bind_partial(*args, **kwargs)
        except TypeError:
            # A call the function itself will reject still gets a span; the
            # function raises inside it, which is the more useful record.
            return {}
        bound.apply_defaults()
        out: dict[str, Any] = {}
        for name in wanted & bound.arguments.keys():
            value = bound.arguments[name]
            # Attribute values must be primitives, so anything else is
            # stringified rather than dropped.
            if not isinstance(value, (str, bool, int, float)):
                value = repr(value)
            out[f"{ARG_ATTRIBUTE_PREFIX}{name}"] = value
        return out

    return bind


def traced(
    name: str | None = None,
    *,
    kind: SpanKind = SpanKind.INTERNAL,
    attributes: Mapping[str, Any] | None = None,
    record_args: Sequence[str] = (),
) -> Callable[[Callable[P, R]], Callable[P, R]]:
    """Create a child span around each call of the decorated function.

    ``name`` defaults to ``module.qualname``. ``record_args`` lists parameter
    names whose values are copied onto the span under ``app.arg.``.
    Exceptions are recorded, the span status is set to ERROR, and the
    exception is re-raised.

    Usable bare (``@traced``) or with arguments (``@traced("x")``).
    Generator functions are rejected at decoration time.
    """
    # Support @traced without parentheses.
    if callable(name):
        return traced()(name)  # type: ignore[arg-type]

    def decorate(func: Callable[P, R]) -> Callable[P, R]:
        _reject_generators(func)
        span_name = name or _qualified_name(func)
        static_attrs = dict(attributes or {})
        static_attrs.setdefault("code.function.name", _qualified_name(func))
        bind_args = _arg_attribute_binder(func, record_args)

        def start(args: tuple[Any, ...], kwargs: dict[str, Any]):
            attrs = {**static_attrs, **bind_args(args, kwargs)}
            return _tracer.start_as_current_span(span_name, kind=kind, attributes=attrs)

        if inspect.iscoroutinefunction(func):

            @functools.wraps(func)
            async def async_wrapper(*args: P.args, **kwargs: P.kwargs) -> R:
                with start(args, kwargs):
                    return await func(*args, **kwargs)

            return async_wrapper  # type: ignore[return-value]

        @functools.wraps(func)
        def wrapper(*args: P.args, **kwargs: P.kwargs) -> R:
            with start(args, kwargs):
                return func(*args, **kwargs)

        return wrapper

    return decorate


def job_run(
    job_name: str,
    *,
    attributes: Mapping[str, Any] | None = None,
    swallow_exceptions: bool = False,
) -> Callable[[Callable[P, R]], Callable[P, R | None]]:
    """Make each call of the decorated function a complete, standalone trace.

    Intended for the body of a polling loop or a scheduled job. The span is
    started with an empty context so it never becomes a child of a stale
    span left active by the surrounding loop or scheduler. Each run records
    ``job.name``; success sets status OK and failure sets status ERROR with
    the exception recorded, so outcomes are queryable by span status.

    With ``swallow_exceptions=True`` the error is recorded on the span and
    the function returns ``None`` instead of raising, which keeps a poller
    alive without each team re-implementing the try/except.
    """

    def decorate(func: Callable[P, R]) -> Callable[P, R | None]:
        _reject_generators(func)
        attrs = {"job.name": job_name, **(attributes or {})}

        @contextmanager
        def run() -> Iterator[Span]:
            # The SDK records the exception and sets ERROR only for
            # exceptions that leave its block, so the catch must sit outside
            # the span context. This layer adds the explicit OK and the
            # swallow choice.
            try:
                with _tracer.start_as_current_span(
                    job_name, context=context.Context(), attributes=attrs
                ) as span:
                    yield span
                    span.set_status(Status(StatusCode.OK))
            except Exception:  # noqa: BLE001
                if not swallow_exceptions:
                    raise

        if inspect.iscoroutinefunction(func):

            @functools.wraps(func)
            async def async_wrapper(*args: P.args, **kwargs: P.kwargs) -> R | None:
                with run():
                    return await func(*args, **kwargs)
                return None

            return async_wrapper  # type: ignore[return-value]

        @functools.wraps(func)
        def wrapper(*args: P.args, **kwargs: P.kwargs) -> R | None:
            with run():
                return func(*args, **kwargs)
            return None

        return wrapper

    return decorate


def poll_forever(
    run_once: Callable[[], Any],
    *,
    interval_seconds: float,
    max_runs: int | None = None,
) -> None:
    """Drive a ``job_run``-decorated callable on a fixed interval.

    The sleep happens outside any span, so idle time is not attributed to
    the job. ``max_runs`` exists so examples and tests can terminate.
    """
    runs = 0
    while max_runs is None or runs < max_runs:
        run_once()
        runs += 1
        if max_runs is None or runs < max_runs:
            time.sleep(interval_seconds)
