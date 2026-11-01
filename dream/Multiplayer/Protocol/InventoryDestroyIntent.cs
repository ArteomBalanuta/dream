namespace Dream.Multiplayer.Protocol;

// Destroys the item anchored at bag cell SlotIndex (0..63). ItemInstanceId is only compared with the
// item that is there, so a queued destroy cannot hit whatever moved there since.
public sealed record InventoryDestroyIntent(int SlotIndex, long ItemInstanceId) : IPlayerActionPayload;
