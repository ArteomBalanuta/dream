namespace Dream.Rendering.Hud;

// One thing a vendor sells, as the window shows it: the item kind the buy intent names, the catalog
// display name and the price in gold. Built by the scene from VendorOpenedResult, never drawn raw.
public readonly record struct VendorStockLine(string ItemKind, string DisplayName, long Price);

// What the vendor window shows and remembers (M3): which NPC it is open for and its stock. One instance per scene, created with a parameterless constructor (the
// headless play scene fills fields by it). The scene opens and closes it; the window only reads the
// stock. Nothing here is sent to the server by itself.
public sealed class VendorHudState
{
    public const int MaxStock = 16;
    public const int MaxTitleChars = 32;

    private readonly VendorStockLine[] _stock = new VendorStockLine[MaxStock];

    public bool IsOpen { get; private set; }

    public int NpcActorId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public int StockCount { get; private set; }

    public VendorStockLine StockAt(int index)
    {
        if ((uint)index >= (uint)StockCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _stock[index];
    }

    public bool SellsKind(string? itemKind)
    {
        for (int i = 0; i < StockCount; i++)
        {
            if (string.Equals(_stock[i].ItemKind, itemKind, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Opens for npcActorId with the stock (at most MaxStock lines are kept). Replaces what was open.
    public void Open(int npcActorId, string title, ReadOnlySpan<VendorStockLine> stock)
    {
        IsOpen = true;
        NpcActorId = npcActorId;
        Title = title ?? string.Empty;
        int count = Math.Min(stock.Length, MaxStock);
        stock[..count].CopyTo(_stock);
        Array.Clear(_stock, count, _stock.Length - count);
        StockCount = count;
    }

    public void Close()
    {
        IsOpen = false;
        NpcActorId = 0;
        Title = string.Empty;
        Array.Clear(_stock);
        StockCount = 0;
    }
}
