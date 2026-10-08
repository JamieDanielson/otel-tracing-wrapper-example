// Run all three example services in one process with console export.

using CompanyTracing;
using Example;
using Example.Services;

using var provider = Telemetry.Configure("tracing-wrapper-example");

Tracing.PollForever(() => BillingPoller.SettleInvoicesOnce(), TimeSpan.FromMilliseconds(50), maxRuns: 2);
await InventorySync.SyncOnce();
NotificationSender.DrainOnce();
