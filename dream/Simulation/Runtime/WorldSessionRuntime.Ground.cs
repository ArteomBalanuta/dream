using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using OpenTK.Mathematics;

namespace Dream.Simulation.Runtime;

// Ground items (M3 workstream A): the session-facing side. The state and rules live in
// PlayerInventoryService.Ground.cs; this partial steps them on the tick, serves the pickup action and
// hands the host the changes to broadcast. Ground changes never travel as action results (a kill that
// drops an item runs inside the tick golden); the host sends them as ServerGroundItems.
public sealed partial class WorldSessionRuntime
{
    // Lapses claims and despawns old ground items; called once per tick from EnsureWolfExists (internal so a test can step
    // a long way at once).
    internal void StepGroundItems(ulong tick) => _inventory.StepGroundItems(tick);

    internal bool HasPendingGroundDeltas => _inventory.HasPendingGroundDeltas;

    // Moves the changes since the last drain into the lists (see PlayerInventoryService.DrainGroundDeltas).
    internal void DrainGroundDeltas(List<GroundItemSnapshot> upserts, List<long> removedIds)
        => _inventory.DrainGroundDeltas(upserts, removedIds);

    // Appends every ground item to destination, for a client's Reset.
    internal void CaptureGroundItems(List<GroundItemSnapshot> destination)
        => _inventory.CaptureGroundItems(destination);

    internal int GroundItemCount => _inventory.GroundItemCount;

    // Called by the persistence bridge's world restore, before the first tick.
    internal void RestoreGroundItems(IReadOnlyList<PersistedItemInstance> items)
        => _inventory.RestoreGroundItems(items, CurrentServerTick);

    private AuthoritativeActionResult ProcessGroundPickupAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        GroundPickupIntent intent)
    {
        if (!TryResolveAuthoritativePlayerTarget(message.SessionId, out _, out IAuthoritativePlayerTarget? target)
            || target == null)
        {
            return CreateRejectedActionResult(message, "Ground pickup rejected because the player has no position.");
        }

        InventoryEquipOutcome outcome = RunInventoryAction(
            player,
            static (service, key, request) => service.TryPickUpGround(key, request.ItemInstanceId, request.Position, request.Tick),
            (intent.ItemInstanceId, Position: (Vector3)target.Position, Tick: CurrentServerTick));
        return CompleteInventoryAction(message, player, outcome, "pickup");
    }
}
