namespace Dream.Multiplayer.Protocol;

// Moves the item in backpack slot From to backpack slot To, swapping with what is there.
public sealed record InventoryMoveIntent(int From, int To) : IPlayerActionPayload;
