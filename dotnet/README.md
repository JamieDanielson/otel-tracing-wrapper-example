# Reusable tracing wrapper for OpenTelemetry .NET

A shared library (`CompanyTracing/`) that application teams reference instead
of writing `ActivitySource` code themselves, plus three toy services that use
it. It is the .NET counterpart of the Python example in [`../python/`](../python/).

## What the shared library gives you

| Helper                                                   | Use it on                                   | Produces                                                                 |
| -------------------------------------------------------- | ------------------------------------------- | ------------------------------------------------------------------------ |
| `Tracing.Traced(() => ...)` / `Tracing.TracedAsync(...)` | Any block of sync or async work             | A child span under whatever span is already active                       |
| `Tracing.JobRun("job.name", () => ...)` / `JobRunAsync`  | The body of a polling loop or scheduled job | A new root trace per run, with `job.name` and status Ok or Error         |
| `Tracing.PollForever(fn, interval)`                      | Drives a `JobRun` body on a timer           | Keeps sleep time outside the trace                                       |
| `Tracing.Args(("name", value), ...)`                     | The `tags` argument of either helper        | Selected argument values on the span as `app.arg.*`                      |

`Traced` is a thin layer over `ActivitySource.StartActivity` inside a `using`.
The layer adds the things teams otherwise re-implement or forget: a default
span name from the calling member (via `[CallerMemberName]`), exception
recording, and a single `ActivitySource` so nobody creates their own. The
exception part matters more in .NET than in Python: disposing an `Activity`
after an exception leaves the span with status Unset and no exception event
unless the caller writes that code.

`JobRun` solves the polling-specific problem. A loop that opens one span and
polls inside it yields one unbounded trace. A loop whose iterations inherit a
stale parent nests every run under the first. `JobRun` clears
`Activity.Current` before starting the span so each run is its own trace, then
restores it, and it sets status Ok or Error on every run so failures are
queryable without reading logs.

The library has no NuGet dependencies. `ActivitySource` is in the base class
library, and it is what the OpenTelemetry .NET SDK listens to. Services pick
the SDK version and exporters.

## Run it

Requires the .NET 10 SDK.

```sh
dotnet run --project Example
```

Console output is one line per span. With `OTEL_EXPORTER_OTLP_ENDPOINT` set,
spans go to that endpoint over OTLP instead (gRPC by default; set
`OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` for HTTP). Any backend that
accepts OTLP works; headers such as API keys go in
`OTEL_EXPORTER_OTLP_HEADERS`:

```sh
export OTEL_EXPORTER_OTLP_ENDPOINT=https://your-collector.example.com
export OTEL_EXPORTER_OTLP_HEADERS="x-api-key=..."
dotnet run --project Example
```

To use the library in your own service, reference the `CompanyTracing`
project (or pack it) and rename it to fit your conventions.

```sh
dotnet test
```

## With zero-code auto-instrumentation

`Example/Telemetry.cs` exists only so the example is self-contained. In a real
service delete it and run under the
[OpenTelemetry .NET automatic instrumentation](https://opentelemetry.io/docs/zero-code/dotnet/).
Unlike Python, the agent does not pick up custom spans automatically: an
`ActivitySource` produces nothing until something subscribes to it by name.
Add the source to the agent's allow list:

```sh
export OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES=CompanyTracing
export OTEL_SERVICE_NAME=billing-poller
dotnet YourService.dll
```

The same applies to a manually configured SDK, which is why
`Telemetry.Configure` calls `AddSource(Tracing.SourceName)`.

## Differences from the Python version

- **No decorators.** C# has no runtime decorator mechanism, so the helpers
  take the body as a lambda. The method becomes an expression-bodied member
  that wraps its own body; see `Example/Services/`.
- **Argument recording is explicit.** A decorator can read parameters by
  name. A lambda cannot, so `record_args=["x"]` becomes
  `tags: Tracing.Args(("x", x))`.
- **Default span name is the member name only.** `[CallerMemberName]` is
  resolved at compile time and has no type information. Pass `name:` for a
  qualified name.
- **Task-returning bodies are a compile error on the sync helpers.** Passing
  `async () => ...` to `Traced` would bind to `Traced<Task>` and end the span
  as soon as the task is created. Overloads marked `[Obsolete(error: true)]`
  catch this at compile time and point at `TracedAsync`. The cost: a lambda
  that only throws has no return type and also binds to those overloads. Give
  it a signature, for example by passing a method group.
- **Swallowed errors return `default`, not `null`.** For a `JobRun<int>`
  with `swallowExceptions: true` that is `0`. Check `Activity.Status` or use
  a reference type if the caller needs to distinguish.

## Things the wrapper does not do

- **Fire-and-forget work.** `Activity.Current` is an `AsyncLocal`, so it
  flows into `Task.Run` and thread-pool work on its own. The .NET hazard is
  the reverse of Python's: an un-awaited task started inside a `JobRun`
  inherits the job span and may finish after it has ended. Await it, or start
  it with a span link to the run instead of a parent.
- **Cross-process linkage.** If a poller pulls messages that carry a
  `traceparent`, use a span link to the producer trace rather than making the
  run a child of it. That needs the messaging client's headers, so it belongs
  in a per-queue helper, not here.
- **Metrics.** Duration and error counts can be derived from spans. If you
  want a counter or histogram, add it next to `SetStatus` in `JobRunCore`,
  where the outcome is known.
