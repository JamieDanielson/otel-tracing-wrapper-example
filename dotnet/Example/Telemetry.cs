using System.Diagnostics;
using CompanyTracing;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Example;

/// <summary>
/// SDK bootstrap for running the example locally.
/// <para>
/// In production this file does not exist: run the service under the
/// OpenTelemetry .NET automatic instrumentation with
/// <c>OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES=CompanyTracing</c> and the
/// same helpers emit to whatever exporter the environment configures. Nothing
/// in <c>CompanyTracing</c> or <c>Services/</c> references this class.
/// </para>
/// </summary>
internal static class Telemetry
{
    public static TracerProvider Configure(string serviceName)
    {
        var builder = Sdk.CreateTracerProviderBuilder()
            .ConfigureResource(r => r.AddService(serviceName))
            .AddSource(Tracing.SourceName);

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
        {
            builder.AddOtlpExporter();
        }
        else
        {
            builder.AddProcessor(new SimpleActivityExportProcessor(new OneLineConsoleExporter()));
        }

        return builder.Build();
    }

    /// <summary>
    /// Prints one line per span. The stock console exporter prints a dozen
    /// lines per span, which hides the parent/child structure the example is
    /// meant to show.
    /// </summary>
    private sealed class OneLineConsoleExporter : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                var tags = activity.TagObjects
                    .Where(t => t.Key.StartsWith("job.") || t.Key.StartsWith("app.arg."))
                    .Select(t => $"{t.Key}={t.Value}");
                Console.WriteLine(
                    $"trace={activity.TraceId} span={activity.SpanId} parent={activity.ParentSpanId} " +
                    $"{activity.Status,-5} {activity.Duration.TotalMilliseconds,7:F1}ms {activity.DisplayName} " +
                    $"{{{string.Join(", ", tags)}}}");
            }

            return ExportResult.Success;
        }
    }
}
