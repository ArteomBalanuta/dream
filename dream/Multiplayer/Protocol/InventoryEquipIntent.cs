namespace Dream.Multiplayer.Protocol;

public sealed record InventoryEquipIntent(int SlotIndex) : IPlayerActionPayload;
