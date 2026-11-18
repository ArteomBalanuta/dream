using Dream.Rendering.Hud;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-CONTENT-1: a vendor's 9th stock entry loaded cleanly and was unbuyable. ContentCatalog allowed 32
// entries, VendorHudState keeps 16 and the vendor window drew eight fixed row plates with no pager, so
// entries past the eighth appeared in no UI at all and nothing said so at load. npcs.json fails to load
// instead.
//
// The window is a grid board now, so the row count is gone and the real constraint is placement: a ware
// is drawn only where VendorGrid.Place finds room for its FOOTPRINT, and one that does not fit is
// unbuyable in exactly the old way. These tests therefore pin the cap against the hud cap and the board,
// and then actually PLACE the shipped stock by footprint rather than counting rows.
public sealed class ContentVendorStockCapTests
{
    // The cap must stay inside what the hud keeps and what the board can hold.
    [Fact]
    public void TheAuthoringCap_FitsTheHudCapAndTheBoard()
    {
        Assert.Equal(8, ContentCatalog.MaxNpcStock);
        Assert.True(ContentCatalog.MaxNpcStock <= VendorHudState.MaxStock);
        Assert.True(ContentCatalog.MaxNpcStock <= VendorGrid.Columns * VendorGrid.Rows);
    }

    // The gate the row count used to stand in for: every authored ware of every shipped vendor really
    // lands on the board at its own footprint, in the same first-fit order the window places them in.
    // A ware that finds no room is drawn nowhere and cannot be bought - the BH-CONTENT-1 shape.
    [Fact]
    public void EveryShippedVendorsStock_PlacesOnTheBoardByItsFootprint()
    {
        ContentCatalog shipped = ContentCatalog.Shipped;
        foreach (NpcDefinition npc in shipped.Npcs.Where(n => n.Stock.Count > 0))
        {
            ulong occupied = 0;
            foreach (NpcStockEntry entry in npc.Stock)
            {
                ItemFootprint footprint = shipped.TryGetItem(entry.ItemKind, out ItemDefinition item)
                    ? item.Footprint
                    : ItemFootprint.One;
                int anchor = BagGrid.FirstFit(occupied, footprint);
                Assert.True(
                    anchor >= 0 && BagGrid.TryMask(anchor, footprint, out ulong mask),
                    $"{npc.Key} sells {entry.ItemKind} ({footprint.Cols}x{footprint.Rows}), which finds no room on the shop board");
                BagGrid.TryMask(anchor, footprint, out ulong placed);
                occupied |= placed;
            }
        }
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
        Assert.True(hud.StockCount <= ContentCatalog.MaxNpcStock);
        for (int n = 0; n < hud.StockCount; n++)
        {
            Assert.True(hud.SellsKind(vendor.Stock[n].ItemKind));
        }
    }
}
