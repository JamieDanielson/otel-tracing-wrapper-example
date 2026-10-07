from __future__ import annotations

import asyncio
import unittest

from opentelemetry import trace
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.sdk.trace.export.in_memory_span_exporter import (
    InMemorySpanExporter,
)
from opentelemetry.trace import StatusCode

from company_tracing import job_run, traced

_exporter = InMemorySpanExporter()
_provider = TracerProvider()
_provider.add_span_processor(SimpleSpanProcessor(_exporter))
trace.set_tracer_provider(_provider)


class TracedTests(unittest.TestCase):
    def setUp(self) -> None:
        _exporter.clear()

    def test_default_name_and_arg_attributes(self) -> None:
        @traced(record_args=["x"])
        def add(x: int, y: int) -> int:
            return x + y

        self.assertEqual(add(2, y=3), 5)
        (span,) = _exporter.get_finished_spans()
        self.assertEqual(span.name, f"{__name__}.TracedTests.test_default_name_and_arg_attributes.<locals>.add")
        self.assertEqual(span.attributes["app.arg.x"], 2)
        self.assertNotIn("app.arg.y", span.attributes)

    def test_bare_decorator_and_async(self) -> None:
        @traced
        async def work() -> str:
            return "ok"

        self.assertEqual(asyncio.run(work()), "ok")
        (span,) = _exporter.get_finished_spans()
        self.assertTrue(span.name.endswith("work"))

    def test_exception_sets_error_and_reraises(self) -> None:
        @traced("boom")
        def fail() -> None:
            raise ValueError("nope")

        with self.assertRaises(ValueError):
            fail()
        (span,) = _exporter.get_finished_spans()
        self.assertEqual(span.status.status_code, StatusCode.ERROR)
        self.assertEqual(span.events[0].name, "exception")

    def test_nests_under_active_span(self) -> None:
        @traced("child")
        def inner() -> None:
            pass

        with trace.get_tracer("t").start_as_current_span("parent") as parent:
            inner()
        child, _ = _exporter.get_finished_spans()
        self.assertEqual(child.parent.span_id, parent.get_span_context().span_id)

    def test_bad_call_still_produces_span(self) -> None:
        @traced(record_args=["x"])
        def f(x: int) -> int:
            return x

        with self.assertRaises(TypeError):
            f()  # type: ignore[call-arg]
        (span,) = _exporter.get_finished_spans()
        self.assertEqual(span.status.status_code, StatusCode.ERROR)

    def test_rejects_string_record_args_and_generators(self) -> None:
        with self.assertRaises(TypeError):

            @traced(record_args="x")
            def f(x: int) -> int:
                return x

        with self.assertRaises(TypeError):

            @traced
            def gen():
                yield 1


class JobRunTests(unittest.TestCase):
    def setUp(self) -> None:
        _exporter.clear()

    def test_each_run_is_a_new_root_trace(self) -> None:
        @job_run("job.a")
        def run() -> None:
            pass

        with trace.get_tracer("t").start_as_current_span("stale-outer"):
            run()
            run()
        spans = [s for s in _exporter.get_finished_spans() if s.name == "job.a"]
        self.assertEqual(len(spans), 2)
        self.assertIsNone(spans[0].parent)
        self.assertIsNone(spans[1].parent)
        self.assertNotEqual(spans[0].context.trace_id, spans[1].context.trace_id)
        self.assertEqual(spans[0].attributes["job.name"], "job.a")
        self.assertEqual(spans[0].status.status_code, StatusCode.OK)

    def test_swallow_exceptions_records_error_and_returns_none(self) -> None:
        @job_run("job.b", swallow_exceptions=True)
        def run() -> int:
            raise RuntimeError("downstream")

        self.assertIsNone(run())
        (span,) = _exporter.get_finished_spans()
        self.assertEqual(span.status.status_code, StatusCode.ERROR)
        self.assertEqual(span.events[0].attributes["exception.type"], "RuntimeError")

    def test_reraises_by_default_and_async(self) -> None:
        @job_run("job.c")
        async def run() -> None:
            raise RuntimeError("downstream")

        with self.assertRaises(RuntimeError):
            asyncio.run(run())
        (span,) = _exporter.get_finished_spans()
        self.assertEqual(span.status.status_code, StatusCode.ERROR)


if __name__ == "__main__":
    unittest.main()
