namespace Dream.Multiplayer.Protocol;

// M3: one item a vendor sells, at its price in gold.
public readonly record struct VendorStockEntry(string ItemKind, long Price);

// M3: the answer to NpcInteractIntent. Carries no inventory snapshot.
public sealed record VendorOpenedResult(
    int NpcActorId,
    string DisplayName,
    IReadOnlyList<VendorStockEntry> Stock) : IActionResultPayload;
