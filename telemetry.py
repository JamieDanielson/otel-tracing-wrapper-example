"""SDK bootstrap for running the example locally.

In production this file does not exist: run the services under
``opentelemetry-instrument`` and the same decorators emit to whatever
exporter the environment configures. Nothing in ``company_tracing`` or
``services/`` imports from here.
"""

from __future__ import annotations

import os

from opentelemetry import trace
from opentelemetry.sdk.resources import SERVICE_NAME, Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import (
    BatchSpanProcessor,
    ConsoleSpanExporter,
    SimpleSpanProcessor,
)


def configure(service_name: str) -> TracerProvider:
    provider = TracerProvider(
        resource=Resource.create({SERVICE_NAME: service_name})
    )
    if os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT"):
        from opentelemetry.exporter.otlp.proto.http.trace_exporter import (
            OTLPSpanExporter,
        )

        provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))
    else:
        provider.add_span_processor(
            SimpleSpanProcessor(ConsoleSpanExporter(formatter=_one_line))
        )
    trace.set_tracer_provider(provider)
    return provider


def _one_line(span) -> str:  # type: ignore[no-untyped-def]
    ctx = span.get_span_context()
    parent = span.parent.span_id if span.parent else 0
    dur_ms = (span.end_time - span.start_time) / 1e6
    attrs = {
        k: v
        for k, v in span.attributes.items()
        if k.startswith(("job.", "app.arg."))
    }
    return (
        f"trace={ctx.trace_id:032x} span={ctx.span_id:016x} "
        f"parent={parent:016x} {span.status.status_code.name:<5} "
        f"{dur_ms:7.1f}ms {span.name} {attrs}\n"
    )
