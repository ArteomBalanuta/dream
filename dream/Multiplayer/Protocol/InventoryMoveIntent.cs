namespace Dream.Multiplayer.Protocol;

// Moves the item anchored at bag cell From so that its top-left cell is on bag cell To (both 0..63),
// swapping with the one item it lands on when there is exactly one (BagGrid.PlanMove).
public sealed record InventoryMoveIntent(int From, int To) : IPlayerActionPayload;
