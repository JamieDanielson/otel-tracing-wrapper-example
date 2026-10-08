using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CompanyTracing;

/// <summary>
/// Shared tracing helpers for internal services.
/// <para>
/// <see cref="Traced(Action, string?, ActivityKind, IEnumerable{KeyValuePair{string, object?}}?, string)"/>
/// wraps a block of code in a span that is a child of whatever span is already
/// active (HTTP request, message handler, parent job).
/// <see cref="JobRun(string, Action, IEnumerable{KeyValuePair{string, object?}}?, bool)"/>
/// wraps one execution of a polling or scheduled job in a fresh root span, so
/// every run is its own trace instead of one unbounded span.
/// </para>
/// <para>
/// Both work with the OpenTelemetry .NET automatic instrumentation and with a
/// manually configured SDK. Spans come from a single <see cref="ActivitySource"/>
/// and are no-ops until something listens to it.
/// </para>
/// </summary>
public static class Tracing
{
    /// <summary>
    /// Name of the <see cref="ActivitySource"/> every span is created from.
    /// An SDK must subscribe to this name (for example with
    /// <c>AddSource(Tracing.SourceName)</c>) or spans are never created.
    /// </summary>
    public const string SourceName = "CompanyTracing";

    /// <summary>
    /// Custom attributes live under a company-owned prefix. <c>code.*</c> is a
    /// semantic-conventions namespace and may gain keys that collide with ours.
    /// </summary>
    public const string ArgTagPrefix = "app.arg.";

    private static readonly ActivitySource Source = new(SourceName, "0.1.0");

    /// <summary>
    /// Builds span tags for selected call arguments, each key prefixed with
    /// <see cref="ArgTagPrefix"/>. The .NET replacement for a decorator that
    /// reads parameters by name: the caller names them explicitly.
    /// </summary>
    public static KeyValuePair<string, object?>[] Args(params (string Name, object? Value)[] args)
    {
        var tags = new KeyValuePair<string, object?>[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            tags[i] = new(ArgTagPrefix + args[i].Name, args[i].Value);
        }

        return tags;
    }

    /// <summary>
    /// Runs <paramref name="body"/> inside a child span of the current activity.
    /// <paramref name="name"/> defaults to the calling member's name. Exceptions
    /// are recorded as an event, set the span status to Error, and are rethrown.
    /// </summary>
    public static void Traced(
        Action body,
        string? name = null,
        ActivityKind kind = ActivityKind.Internal,
        IEnumerable<KeyValuePair<string, object?>>? tags = null,
        [CallerMemberName] string caller = "")
    {
        TracedCore<object?>(() => { body(); return null; }, name, kind, tags, caller);
    }

    /// <inheritdoc cref="Traced(Action, string?, ActivityKind, IEnumerable{KeyValuePair{string, object?}}?, string)"/>
    public static T Traced<T>(
        Func<T> body,
        string? name = null,
        ActivityKind kind = ActivityKind.Internal,
        IEnumerable<KeyValuePair<string, object?>>? tags = null,
        [CallerMemberName] string caller = "")
    {
        return TracedCore(body, name, kind, tags, caller);
    }

    private static T TracedCore<T>(Func<T> body, string? name, ActivityKind kind, IEnumerable<KeyValuePair<string, object?>>? tags, string caller)
    {
        using var activity = StartChild(name ?? caller, kind, tags, caller);
        try
        {
            return body();
        }
        catch (Exception ex)
        {
            RecordFailure(activity, ex);
            throw;
        }
    }

    /// <inheritdoc cref="Traced(Action, string?, ActivityKind, IEnumerable{KeyValuePair{string, object?}}?, string)"/>
    public static Task TracedAsync(
        Func<Task> body,
        string? name = null,
        ActivityKind kind = ActivityKind.Internal,
        IEnumerable<KeyValuePair<string, object?>>? tags = null,
        [CallerMemberName] string caller = "")
    {
        return TracedAsyncCore<object?>(async () => { await body().ConfigureAwait(false); return null; }, name, kind, tags, caller);
    }

    /// <inheritdoc cref="Traced(Action, string?, ActivityKind, IEnumerable{KeyValuePair{string, object?}}?, string)"/>
    public static Task<T> TracedAsync<T>(
        Func<Task<T>> body,
        string? name = null,
        ActivityKind kind = ActivityKind.Internal,
        IEnumerable<KeyValuePair<string, object?>>? tags = null,
        [CallerMemberName] string caller = "")
    {
        return TracedAsyncCore(body, name, kind, tags, caller);
    }

    private static async Task<T> TracedAsyncCore<T>(Func<Task<T>> body, string? name, ActivityKind kind, IEnumerable<KeyValuePair<string, object?>>? tags, string caller)
    {
        using var activity = StartChild(name ?? caller, kind, tags, caller);
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordFailure(activity, ex);
            throw;
        }
    }

    // A Task-returning lambda passed to the synchronous overload would bind to
    // Traced<Task> and end the span before the work completes. These overloads
    // are more specific than Func<T>, so overload resolution picks them and
    // the error attribute turns the mistake into a compile error.
    [Obsolete("Use TracedAsync for a Task-returning body.", error: true)]
    public static Task Traced(Func<Task> body, string? name = null, ActivityKind kind = ActivityKind.Internal, IEnumerable<KeyValuePair<string, object?>>? tags = null, [CallerMemberName] string caller = "")
        => throw new NotSupportedException();

    [Obsolete("Use TracedAsync for a Task-returning body.", error: true)]
    public static Task<T> Traced<T>(Func<Task<T>> body, string? name = null, ActivityKind kind = ActivityKind.Internal, IEnumerable<KeyValuePair<string, object?>>? tags = null, [CallerMemberName] string caller = "")
        => throw new NotSupportedException();

    /// <summary>
    /// Runs <paramref name="body"/> as one complete, standalone trace.
    /// <para>
    /// Intended for the body of a polling loop or a scheduled job. The span is
    /// started with no parent so it never becomes a child of a stale span left
    /// active by the surrounding loop or scheduler. Each run records
    /// <c>job.name</c>; success sets status Ok and failure sets status Error
    /// with the exception recorded, so outcomes are queryable by span status.
    /// </para>
    /// <para>
    /// With <paramref name="swallowExceptions"/> the error is recorded on the
    /// span and the call returns normally instead of throwing, which keeps a
    /// poller alive without each team re-implementing the try/catch.
    /// </para>
    /// </summary>
    public static void JobRun(
        string jobName,
        Action body,
        IEnumerable<KeyValuePair<string, object?>>? tags = null,
        bool swallowExceptions = false)
    {
        JobRunCore<object?>(jobName, () => { body(); return null; }, tags, swallowExceptions);
    }

    /// <inheritdoc cref="JobRun(string, Action, IEnumerable{KeyValuePair{string, object?}}?, bool)"/>
    /// <returns>The body's result, or <see langword="default"/> when an exception was swallowed.</returns>
    public static T? JobRun<T>(
        string jobName,
        Func<T> body,
        IEnumerable<KeyValuePair<string, object?>>? tags = null,
        bool swallowExceptions = false)
    {
        return JobRunCore(jobName, body, tags, swallowExceptions);
    }

    private static T? JobRunCore<T>(string jobName, Func<T> body, IEnumerable<KeyValuePair<string, object?>>? tags, bool swallowExceptions)
    {
        // Activity.Current is the implicit parent for new activities. Clearing
        // it is the only way to start a root; passing a default parent context
        // falls back to Activity.Current. Restored so the caller's context is
        // unchanged after the run.
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            using var activity = StartRoot(jobName, tags);
            try
            {
                var result = body();
                activity?.SetStatus(ActivityStatusCode.Ok);
                return result;
            }
            catch (Exception ex)
            {
                RecordFailure(activity, ex);
                if (!swallowExceptions)
                {
                    throw;
                }

                return default;
            }
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    /// <inheritdoc cref="JobRun(string, Action, IEnumerable{KeyValuePair{string, object?}}?, bool)"/>
    public static Task JobRunAsync(
        string jobName,
        Func<Task> body,
        IEnumerable<KeyValuePair<string, object?>>? tags = null,
        bool swallowExceptions = false)
    {
        return JobRunAsyncCore<object?>(jobName, async () => { await body().ConfigureAwait(false); return null; }, tags, swallowExceptions);
    }

    /// <inheritdoc cref="JobRun{T}(string, Func{T}, IEnumerable{KeyValuePair{string, object?}}?, bool)"/>
    public static Task<T?> JobRunAsync<T>(
        string jobName,
        Func<Task<T>> body,
        IEnumerable<KeyValuePair<string, object?>>? tags = null,
        bool swallowExceptions = false)
    {
        return JobRunAsyncCore(jobName, body, tags, swallowExceptions);
    }

    private static async Task<T?> JobRunAsyncCore<T>(string jobName, Func<Task<T>> body, IEnumerable<KeyValuePair<string, object?>>? tags, bool swallowExceptions)
    {
        // Activity.Current is AsyncLocal: a change inside an async method flows
        // to awaited callees but not back to the caller, so the caller already
        // sees its own context once this method returns. Restoring anyway keeps
        // the synchronous and asynchronous paths identical.
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            using var activity = StartRoot(jobName, tags);
            try
            {
                var result = await body().ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return result;
            }
            catch (Exception ex)
            {
                RecordFailure(activity, ex);
                if (!swallowExceptions)
                {
                    throw;
                }

                return default;
            }
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Obsolete("Use JobRunAsync for a Task-returning body.", error: true)]
    public static Task JobRun(string jobName, Func<Task> body, IEnumerable<KeyValuePair<string, object?>>? tags = null, bool swallowExceptions = false)
        => throw new NotSupportedException();

    [Obsolete("Use JobRunAsync for a Task-returning body.", error: true)]
    public static Task<T> JobRun<T>(string jobName, Func<Task<T>> body, IEnumerable<KeyValuePair<string, object?>>? tags = null, bool swallowExceptions = false)
        => throw new NotSupportedException();

    /// <summary>
    /// Drives a <see cref="JobRun(string, Action, IEnumerable{KeyValuePair{string, object?}}?, bool)"/>
    /// body on a fixed interval. The sleep happens outside any span, so idle
    /// time is not attributed to the job. <paramref name="maxRuns"/> exists so
    /// examples and tests can terminate.
    /// </summary>
    public static void PollForever(Action runOnce, TimeSpan interval, int? maxRuns = null)
    {
        var runs = 0;
        while (maxRuns is null || runs < maxRuns)
        {
            runOnce();
            runs++;
            if (maxRuns is null || runs < maxRuns)
            {
                Thread.Sleep(interval);
            }
        }
    }

    private static Activity? StartChild(string name, ActivityKind kind, IEnumerable<KeyValuePair<string, object?>>? tags, string caller)
    {
        // Tags are passed at start so samplers can see them. A null result
        // means no listener wants this span (no SDK, or sampled out); the body
        // still runs and every later call on the activity is null-conditional.
        var activity = Source.StartActivity(name, kind, parentContext: default, tags: tags);
        activity?.SetTag("code.function.name", caller);
        return activity;
    }

    private static Activity? StartRoot(string jobName, IEnumerable<KeyValuePair<string, object?>>? tags)
    {
        var allTags = new List<KeyValuePair<string, object?>> { new("job.name", jobName) };
        if (tags is not null)
        {
            allTags.AddRange(tags);
        }

        return Source.StartActivity(jobName, ActivityKind.Internal, parentContext: default, tags: allTags);
    }

    private static void RecordFailure(Activity? activity, Exception ex)
    {
        // The SDK does not record exceptions or set Error on its own in .NET;
        // this is the part teams otherwise forget.
        if (activity is null)
        {
            return;
        }

        activity.AddException(ex);
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
    }
}
