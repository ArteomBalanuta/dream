using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;

namespace Dream.Client.Scenes;

// Quests on the client (M6 workstream B): the tracker's log and the quest giver's dialogue. The server is the
// authority for all of it; this side keeps what it was told, shows it, and sends one intent per click
// (through the HUD intent queue). The state is QuestHudState, a class the headless play scene can fill.
public partial class PlayScene
{
    // Once a frame: ends a finished session's quests, and closes the dialogue window past the interact range
    // plus a margin, like the vendor, or when it no longer fits (dead, inventory closed, NPC unknown).
    private void UpdateQuestHud()
    {
        if (!_remoteClient.IsConnected)
        {
            _questHud.Reset();
            return;
        }

        if (!_questHud.DialogueOpen)
        {
            return;
        }

        if (_deathTracker.IsDead
            || _vendorHud.IsOpen
            || !_settings.InventoryVisible
            || !_npcs.TryGet(_questHud.NpcActorId, out ClientNpc npc)
            || HorizontalDistance(_runtime.Player.GetWorldPosition(), npc.Position) > ClientNpcCache.InteractRange + VendorCloseMargin)
        {
            _questHud.CloseDialogue();
        }
    }

    private void ApplyQuestLog(ServerQuestLog log) => _questHud.SetLog(log?.Entries);

    // The answer to NpcInteractIntent for a quest giver. Wire data is hostile: the NPC must be one the client
    // knows and is near, and the lines are cleaned and capped by the state.
    private void ApplyNpcDialogue(NpcDialogueResult dialogue)
    {
        if (dialogue is null
            || _deathTracker.IsDead
            || !_npcs.TryGet(dialogue.NpcActorId, out ClientNpc npc)
            || HorizontalDistance(_runtime.Player.GetWorldPosition(), npc.Position) > ClientNpcCache.InteractRange + VendorCloseMargin
            || _settings.SettingsVisible)
        {
            return;
        }

        // The window sits over the inventory, in the place of the character window: it opens it.
        if (!_settings.InventoryVisible)
        {
            _settings.ToggleInventory();
        }

        _vendorHud.Close();
        string name = string.IsNullOrWhiteSpace(dialogue.NpcName) ? npc.DisplayName : ClientNpcCache.CleanName(dialogue.NpcName);
        _questHud.OpenDialogue(npc.ActorId, name, dialogue.Greeting, dialogue.Quests);
    }
}
