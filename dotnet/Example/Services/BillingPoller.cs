using CompanyTracing;

namespace Example.Services;

/// <summary>Billing team: polls a payment provider for settled invoices.</summary>
internal static class BillingPoller
{
    private static List<string> FetchSettledInvoices(int batchSize) => Tracing.Traced(
        () =>
        {
            Thread.Sleep(10);
            return Enumerable.Range(0, batchSize).Select(_ => $"inv-{Random.Shared.Next(1000, 9999)}").ToList();
        },
        tags: Tracing.Args(("batch_size", batchSize)));

    private static void MarkPaid(string invoiceId) => Tracing.Traced(
        () =>
        {
            Thread.Sleep(2);
            if (invoiceId.EndsWith('7'))
            {
                throw new InvalidOperationException($"ledger rejected {invoiceId}");
            }
        },
        tags: Tracing.Args(("invoice_id", invoiceId)));

    public static int SettleInvoicesOnce() => Tracing.JobRun(
        "billing.settle_invoices",
        () =>
        {
            var invoices = FetchSettledInvoices(batchSize: 3);
            foreach (var invoiceId in invoices)
            {
                MarkPaid(invoiceId);
            }

            return invoices.Count;
        },
        swallowExceptions: true);
}
