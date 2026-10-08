# Reusable tracing wrapper examples

The same idea in two languages: a shared module that application teams use
instead of writing OpenTelemetry span code themselves, plus three toy services
that use it. Each directory is self-contained and has its own README.

| Directory               | Language                 | Wrapper surface                                        |
| ----------------------- | ------------------------ | ------------------------------------------------------ |
| [`python/`](python/)    | Python 3.10+             | `@traced`, `@job_run`, `poll_forever`                  |
| [`dotnet/`](dotnet/)    | .NET 10 (C#)             | `Tracing.Traced`, `Tracing.JobRun`, `Tracing.PollForever` |

Both solve the same two problems. `traced` adds a child span around a unit of
work under whatever span is already active. `job_run` makes each run of a
polling loop or scheduled job its own root trace with an OK or ERROR status,
instead of one unbounded span or a chain of runs nested under the first.
