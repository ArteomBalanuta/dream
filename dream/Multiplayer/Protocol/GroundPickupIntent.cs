namespace Dream.Multiplayer.Protocol;

// M3: pick up a ground item. The server checks that it still lies there, the claim, the range and the
// bag room, and answers with InventoryChangedResult.
public sealed record GroundPickupIntent(long ItemInstanceId) : IPlayerActionPayload;
