using Dream.Simulation.Inventory;

namespace Dream.Multiplayer.Protocol;

public sealed record InventoryChangedResult(PlayerInventorySnapshot Inventory) : IActionResultPayload;
