# Reusable tracing wrapper for opentelemetry-python

A shared module (`company_tracing/`) that application teams import instead of
writing OpenTelemetry span code themselves, plus three toy services that use it.

## What the shared module gives you

| Decorator                                        | Use it on                                   | Produces                                                                         |
| ------------------------------------------------ | ------------------------------------------- | -------------------------------------------------------------------------------- |
| `@traced` / `@traced("name", record_args=[...])` | Any sync or async function                  | A child span under whatever span is already active                               |
| `@job_run("job.name")`                           | The body of a polling loop or scheduled job | A new root trace per run, with `job.name` and status OK or ERROR                  |
| `poll_forever(fn, interval_seconds=...)`         | Drives a `job_run` function on a timer      | Keeps sleep time outside the trace                                               |

`traced` is a thin layer over `tracer.start_as_current_span`, which the
OpenTelemetry API already allows as a decorator. The layer adds the things
teams otherwise re-implement: a default span name from the function's qualified
name, selected arguments copied onto the span as `app.arg.*`, and a single tracer so nobody
has to call `trace.get_tracer`.

`job_run` solves the polling-specific problem. A loop that opens one span and
polls inside it yields one unbounded trace. A loop whose iterations inherit a
stale parent nests every run under the first. `job_run` starts each run from an
empty context so each run is its own trace, and it sets span status OK or ERROR
on every run so failures are queryable without reading logs.

## Run it

Requires Python 3.10 or newer.

```sh
pip install -r requirements.txt
python main.py
```

Console output is one line per span. With `OTEL_EXPORTER_OTLP_ENDPOINT` set,
spans go to that endpoint over OTLP/HTTP instead. Any backend that accepts OTLP
works; headers such as API keys go in `OTEL_EXPORTER_OTLP_HEADERS`:

```sh
export OTEL_EXPORTER_OTLP_ENDPOINT=https://your-collector.example.com
export OTEL_EXPORTER_OTLP_HEADERS="x-api-key=..."
python main.py
```

To use the module in your own service, copy the `company_tracing/` directory
into your codebase (or package it) and rename it to fit your conventions.

```sh
python -m unittest discover -s tests -v
```

## With zero-code auto-instrumentation

`telemetry.py` exists only so the example is self-contained. In a real service
delete it and run under the agent. The decorators resolve the tracer through the
global API, so they pick up whatever provider the agent installs:

```sh
opentelemetry-instrument --service_name billing-poller python -m your_entrypoint
```

## Things the wrapper does not do

- **Thread pools.** If a job fans work out to `ThreadPoolExecutor`, child spans
  land in separate traces unless context is propagated. Enable
  `opentelemetry-instrumentation-threading` (`ThreadingInstrumentor().instrument()`),
  which patches `Thread.start` and `Executor.submit` to carry context.
- **Cross-process linkage.** If a poller pulls messages that carry a
  `traceparent`, use a span link to the producer trace rather than making the
  run a child of it. That needs the messaging client's headers, so it belongs in
  a per-queue helper, not here.
- **Metrics.** Duration and error counts can be derived from spans. If you want
  a counter or histogram, add it in the `run` context manager inside `job_run`,
  where the outcome is known.
