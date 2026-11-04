namespace Dream.Multiplayer.Protocol;

// Drops the item anchored at bag cell SlotIndex (0..63) onto the ground at the player's own
// authoritative position. The client sends no position: the server reads the dropper's own, the same
// source TryPickUpGround checks its range against, so a drop cannot place an item anywhere the player
// is not. ItemInstanceId is only compared with the item that is there, so a queued drop cannot hit
// whatever moved there since (as InventoryDestroyIntent).
public sealed record InventoryDropIntent(int SlotIndex, long ItemInstanceId) : IPlayerActionPayload;
