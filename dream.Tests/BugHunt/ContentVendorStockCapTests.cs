using Dream.Rendering.Hud;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-CONTENT-1: a vendor's 9th stock entry loaded cleanly and was unbuyable. ContentCatalog allowed 32
// entries, VendorHudState keeps 16 and VendorWindow draws VendorWindow.BuyRows fixed row plates with no
// pager over the buy list (only the sell list pages), so entries past the eighth appeared in no UI at
// all and nothing said so at load. The shipped quartermaster is at 6 of 8, so two more authored items
// would have broken it quietly. npcs.json now fails to load instead - the same shape as the per-giver
// quest cap, and the same reasoning: raising the number means teaching the window to page or scroll,
// which is UI work, not content validation.
public sealed class ContentVendorStockCapTests
{
    // The cap is the window's, so the three numbers must stay in step.
    [Fact]
    public void TheAuthoringCap_IsTheNumberOfBuyRowsTheWindowDraws()
    {
        Assert.Equal(VendorWindow.BuyRows, ContentCatalog.MaxNpcStock);
        Assert.True(ContentCatalog.MaxNpcStock <= VendorHudState.MaxStock);
    }

    // The shipped content still loads, and the quartermaster is under the cap with room to spare.
    [Fact]
    public void TheShippedContent_LoadsAndIsUnderTheCap()
    {
        ContentCatalog shipped = ContentCatalog.Shipped;

        NpcDefinition quartermaster = shipped.Npcs.Single(npc => npc.Key == "spawn-quartermaster");
        Assert.InRange(quartermaster.Stock.Count, 1, ContentCatalog.MaxNpcStock);
        Assert.All(shipped.Npcs, npc => Assert.InRange(npc.Stock.Count, 0, ContentCatalog.MaxNpcStock));
    }

    // Exactly at the limit: accepted, and every entry is kept.
    [Fact]
    public void AVendorAtTheCap_LoadsAndKeepsEveryEntry()
    {
        ContentCatalog catalog = ContentBugHunt.Load(
            itemsJson: ContentBugHunt.ItemsWithVendorFillers(ContentCatalog.MaxNpcStock),
            npcsJson: ContentBugHunt.VendorWithStock(ContentCatalog.MaxNpcStock));

        NpcDefinition vendor = catalog.Npcs.Single(npc => npc.Key == "spawn-quartermaster");
        Assert.Equal(ContentCatalog.MaxNpcStock, vendor.Stock.Count);
    }

    // One past the limit: refused at load, naming the file, the path and the reason.
    [Fact]
    public void AVendorOnePastTheCap_IsRefusedAtLoad()
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(
            () => ContentBugHunt.Load(
                itemsJson: ContentBugHunt.ItemsWithVendorFillers(ContentCatalog.MaxNpcStock + 1),
                npcsJson: ContentBugHunt.VendorWithStock(ContentCatalog.MaxNpcStock + 1)));

        Assert.Contains(ContentCatalog.NpcsFileName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("$.npcs[0].stock", failure.Message, StringComparison.Ordinal);
        Assert.Contains("unbuyable", failure.Message, StringComparison.Ordinal);
    }

    // Control: a vendor at the cap is drawn in full - the hud keeps every line and the window gives a
    // buy row to each, so nothing authored is lost between the catalog and the click.
    [Fact]
    public void AVendorAtTheCap_FitsTheHudAndTheWindowRows()
    {
        ContentCatalog catalog = ContentBugHunt.Load(
            itemsJson: ContentBugHunt.ItemsWithVendorFillers(ContentCatalog.MaxNpcStock),
            npcsJson: ContentBugHunt.VendorWithStock(ContentCatalog.MaxNpcStock));
        NpcDefinition vendor = catalog.Npcs.Single(npc => npc.Key == "spawn-quartermaster");

        VendorStockLine[] lines = [.. vendor.Stock.Select(entry =>
            new VendorStockLine(entry.ItemKind, entry.ItemKind, entry.Price))];
        VendorHudState hud = new();
        hud.Open(npcActorId: 7, "Quartermaster", lines);

        Assert.Equal(vendor.Stock.Count, hud.StockCount);
        Assert.True(hud.StockCount <= VendorWindow.BuyRows);
        for (int n = 0; n < hud.StockCount; n++)
        {
            Assert.True(hud.SellsKind(vendor.Stock[n].ItemKind));
        }
    }
}
