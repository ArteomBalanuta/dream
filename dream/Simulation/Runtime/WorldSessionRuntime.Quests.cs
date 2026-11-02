using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Simulation.Runtime;

// Quests (M6 workstream B). Contract stubs: every quest action is refused and no NPC gives quests
// until workstream B fills this file in.
public sealed partial class WorldSessionRuntime
{
    private AuthoritativeActionResult ProcessQuestAcceptAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        QuestAcceptIntent intent)
        => CompleteInventoryAction(message, player, InventoryEquipOutcome.QuestUnavailable, "quest");

    private AuthoritativeActionResult ProcessQuestTurnInAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        QuestTurnInIntent intent)
        => CompleteInventoryAction(message, player, InventoryEquipOutcome.QuestUnavailable, "quest");

    private AuthoritativeActionResult ProcessQuestAbandonAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        QuestAbandonIntent intent)
        => CompleteInventoryAction(message, player, InventoryEquipOutcome.QuestUnavailable, "quest");

    // Whether the NPC gives quests; then NpcInteractIntent is answered with BuildNpcDialogue.
    private bool HasQuestsFor(SpawnedNpc npc) => false;

    private AuthoritativeActionResult BuildNpcDialogue(PlayerActionMessage message, ConnectedPlayer player, SpawnedNpc npc)
        => CompleteInventoryAction(message, player, InventoryEquipOutcome.QuestUnavailable, "quest");

    // Called once per kill, before the loot, with everyone who damaged the enemy.
    private void RecordEnemyKill(ReadOnlySpan<EnemyDamageContribution> contributors, string enemyKind, Vector3 at)
    {
    }
}
