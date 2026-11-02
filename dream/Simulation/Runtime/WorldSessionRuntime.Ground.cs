using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;

namespace Dream.Simulation.Runtime;

// Ground items (M3 workstream A). Contract stubs: workstream A fills them in.
public sealed partial class WorldSessionRuntime
{
    // Lapses claims and despawns old ground items; called once per tick from EnsureWolfExists.
    private void StepGroundItems(ulong tick)
    {
    }

    private AuthoritativeActionResult ProcessGroundPickupAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        GroundPickupIntent intent)
        => CreateRejectedActionResult(message, "Ground pickup is not available yet.");
}
