"""Notifications team: drains a queue, parent span already active.

Shows that ``traced`` nests under whatever span the caller already opened,
so it composes with auto-instrumented frameworks (Flask, Celery, ...)
as well as with ``job_run``.
"""

from __future__ import annotations

import time

from opentelemetry.trace import SpanKind

from company_tracing import job_run, traced


@traced("notifications.render_template", record_args=["template"])
def render(template: str, user_id: int) -> str:
    time.sleep(0.001)
    return f"<{template} for {user_id}>"


@traced(kind=SpanKind.PRODUCER, attributes={"messaging.system": "sns"})
def publish(payload: str) -> None:
    time.sleep(0.003)


@job_run("notifications.drain_queue")
def drain_once() -> None:
    for user_id in (1, 2):
        publish(render("welcome", user_id))
