using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace CompanyTracing.Tests;

/// <summary>
/// One SDK for the whole test run. ActivitySource listeners are process-wide,
/// so a provider per test class would receive every other class's spans too.
/// Sharing one collection also serialises the tests, which the shared
/// exported list requires.
/// </summary>
public sealed class TracingFixture : IDisposable
{
    public const string OtherSourceName = "test-other-source";

    public List<Activity> Exported { get; } = [];

    public ActivitySource OtherSource { get; } = new(OtherSourceName);

    private readonly TracerProvider provider;

    public TracingFixture()
    {
        provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(Tracing.SourceName, OtherSourceName)
            .AddInMemoryExporter(Exported)
            .Build();
    }

    public void Dispose()
    {
        provider.Dispose();
        OtherSource.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class TracingCollection : ICollectionFixture<TracingFixture>
{
    public const string Name = "tracing";
}

[Collection(TracingCollection.Name)]
public sealed class TracedTests(TracingFixture fixture)
{
    private List<Activity> Exported => fixture.Exported;

    [Fact]
    public void DefaultNameAndArgTags()
    {
        Exported.Clear();

        var result = Tracing.Traced(() => 2 + 3, tags: Tracing.Args(("x", 2)));

        Assert.Equal(5, result);
        var span = Assert.Single(Exported);
        Assert.Equal(nameof(DefaultNameAndArgTags), span.DisplayName);
        Assert.Equal(nameof(DefaultNameAndArgTags), span.GetTagItem("code.function.name"));
        Assert.Equal(2, span.GetTagItem("app.arg.x"));
        Assert.Null(span.GetTagItem("app.arg.y"));
    }

    [Fact]
    public async Task AsyncBodyIsAwaitedInsideSpan()
    {
        Exported.Clear();

        var result = await Tracing.TracedAsync(async () =>
        {
            await Task.Delay(5);
            return "ok";
        });

        Assert.Equal("ok", result);
        var span = Assert.Single(Exported);
        Assert.Equal(nameof(AsyncBodyIsAwaitedInsideSpan), span.DisplayName);
        Assert.True(span.Duration >= TimeSpan.FromMilliseconds(4), $"span ended before the body: {span.Duration}");
    }

    [Fact]
    public void ExceptionSetsErrorAndRethrows()
    {
        Exported.Clear();

        // A lambda that only throws has no return type and would bind to the
        // Task-returning guard overload; a method group carries its signature.
        static void Fail() => throw new InvalidOperationException("nope");

        var ex = Assert.Throws<InvalidOperationException>(() => Tracing.Traced(Fail, name: "boom"));

        Assert.Equal("nope", ex.Message);
        var span = Assert.Single(Exported);
        Assert.Equal("boom", span.DisplayName);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("exception", Assert.Single(span.Events).Name);
    }

    [Fact]
    public void NestsUnderActiveSpan()
    {
        Exported.Clear();

        ActivitySpanId parentId;
        using (var parent = fixture.OtherSource.StartActivity("parent")!)
        {
            parentId = parent.SpanId;
            Tracing.Traced(() => { }, name: "child", kind: ActivityKind.Client);
        }

        var child = Assert.Single(Exported, s => s.DisplayName == "child");
        Assert.Equal(parentId, child.ParentSpanId);
        Assert.Equal(ActivityKind.Client, child.Kind);
    }
}

[Collection(TracingCollection.Name)]
public sealed class JobRunTests(TracingFixture fixture)
{
    private List<Activity> Exported => fixture.Exported;

    [Fact]
    public void EachRunIsANewRootTraceAndRestoresCallerContext()
    {
        Exported.Clear();

        using (var stale = fixture.OtherSource.StartActivity("stale-outer"))
        {
            Tracing.JobRun("job.a", () => { });
            Assert.Same(stale, Activity.Current);
            Tracing.JobRun("job.a", () => { });
        }

        var runs = Exported.Where(s => s.DisplayName == "job.a").ToList();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, s => Assert.Equal(default, s.ParentSpanId));
        Assert.NotEqual(runs[0].TraceId, runs[1].TraceId);
        Assert.All(runs, s => Assert.Equal("job.a", s.GetTagItem("job.name")));
        Assert.All(runs, s => Assert.Equal(ActivityStatusCode.Ok, s.Status));
    }

    [Fact]
    public void SwallowExceptionsRecordsErrorAndReturnsDefault()
    {
        Exported.Clear();

        static string Fail() => throw new InvalidOperationException("downstream");

        var result = Tracing.JobRun("job.b", Fail, swallowExceptions: true);

        Assert.Null(result);
        var span = Assert.Single(Exported);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        var exceptionEvent = Assert.Single(span.Events);
        Assert.Equal(typeof(InvalidOperationException).FullName, exceptionEvent.Tags.Single(t => t.Key == "exception.type").Value);
    }

    [Fact]
    public async Task RethrowsByDefaultAndAsync()
    {
        Exported.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Tracing.JobRunAsync("job.c", async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("downstream");
        }));

        var span = Assert.Single(Exported);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("job.c", span.GetTagItem("job.name"));
    }

    [Fact]
    public void ExtraTagsAreRecordedAlongsideJobName()
    {
        Exported.Clear();

        Tracing.JobRun("job.d", () => { }, tags: [new("inventory.region", "us-east")]);

        var span = Assert.Single(Exported);
        Assert.Equal("job.d", span.GetTagItem("job.name"));
        Assert.Equal("us-east", span.GetTagItem("inventory.region"));
    }
}
