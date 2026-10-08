using System.Diagnostics;
using CompanyTracing;

namespace Example.Services;

/// <summary>
/// Notifications team: drains a queue, parent span already active.
/// <para>
/// Shows that <c>Traced</c> nests under whatever span the caller already
/// opened, so it composes with auto-instrumented frameworks (ASP.NET Core,
/// MassTransit, ...) as well as with <c>JobRun</c>.
/// </para>
/// </summary>
internal static class NotificationSender
{
    private static string Render(string template, int userId) => Tracing.Traced(
        () =>
        {
            Thread.Sleep(1);
            return $"<{template} for {userId}>";
        },
        name: "notifications.render_template",
        tags: Tracing.Args(("template", template)));

    private static void Publish(string payload) => Tracing.Traced(
        () => Thread.Sleep(3),
        kind: ActivityKind.Producer,
        tags: [new("messaging.system", "sns")]);

    public static void DrainOnce() => Tracing.JobRun(
        "notifications.drain_queue",
        () =>
        {
            foreach (var userId in new[] { 1, 2 })
            {
                Publish(Render("welcome", userId));
            }
        });
}
