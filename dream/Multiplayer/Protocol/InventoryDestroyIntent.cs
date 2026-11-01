namespace Dream.Multiplayer.Protocol;

// Destroys the item in backpack slot SlotIndex. ItemInstanceId is only compared with the item that
// is there, so a queued destroy cannot hit whatever moved into the slot since.
public sealed record InventoryDestroyIntent(int SlotIndex, long ItemInstanceId) : IPlayerActionPayload;
