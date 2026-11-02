using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;

namespace Dream.Simulation.Runtime;

// Consumables, the quickbar and mana (M4 workstream A). Contract-commit stubs: the actions reject
// until workstream A replaces this file.
public sealed partial class WorldSessionRuntime
{
    private AuthoritativeActionResult ProcessItemUseAction(
        PlayerActionMessage message,
        AuthoritativeSessionActionContext context,
        ItemUseIntent intent)
    {
        return CreateRejectedActionResult(message, "Item use is not available yet.");
    }

    private AuthoritativeActionResult ProcessQuickbarBindAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        QuickbarBindIntent intent)
    {
        return CreateRejectedActionResult(message, "Quickbar binding is not available yet.");
    }

    // Once per gameplay tick, after AdvanceRespawns: regenerates mana. A no-op until workstream A.
    private void AdvanceMana()
    {
    }
}
