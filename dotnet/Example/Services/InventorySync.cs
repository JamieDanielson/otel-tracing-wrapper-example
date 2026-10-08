using CompanyTracing;

namespace Example.Services;

/// <summary>Inventory team: async sync against a warehouse API.</summary>
internal static class InventorySync
{
    private static Task<Dictionary<string, int>> FetchStockLevels(string warehouse) => Tracing.TracedAsync(
        async () =>
        {
            await Task.Delay(10);
            return new Dictionary<string, int> { ["sku-1"] = 4, ["sku-2"] = 0 };
        },
        tags: Tracing.Args(("warehouse", warehouse)));

    private static Task WriteStockLevels(Dictionary<string, int> levels) => Tracing.TracedAsync(
        async () => await Task.Delay(5));

    public static Task SyncOnce() => Tracing.JobRunAsync(
        "inventory.sync",
        async () =>
        {
            var levels = await FetchStockLevels(warehouse: "wh-7");
            await WriteStockLevels(levels);
        },
        tags: [new("inventory.region", "us-east")]);
}
