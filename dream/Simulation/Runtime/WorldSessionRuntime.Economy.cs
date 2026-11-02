using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;

namespace Dream.Simulation.Runtime;

// Gold and vendors (M3 workstream B). Contract stubs: workstream B fills them in.
public sealed partial class WorldSessionRuntime
{
    // Credits the killer's wallet with the kill's gold; called by GrantLoot once the killer's inventory
    // is known to be loaded. seedHash is the hash of the killer's session id.
    private void GrantKillGold(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick, ulong seedHash)
    {
    }

    private AuthoritativeActionResult ProcessNpcInteractAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        NpcInteractIntent intent)
        => CreateRejectedActionResult(message, "Vendors are not available yet.");

    private AuthoritativeActionResult ProcessVendorBuyAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        VendorBuyIntent intent)
        => CreateRejectedActionResult(message, "Vendors are not available yet.");

    private AuthoritativeActionResult ProcessVendorSellAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        VendorSellIntent intent)
        => CreateRejectedActionResult(message, "Vendors are not available yet.");
}
