using Dream.Multiplayer.Protocol;

namespace Dream.Client.Scenes;

// Quests on the client (M6 workstream B). Contract stubs: the messages are accepted and ignored until
// workstream B fills them in.
public partial class PlayScene
{
    // Once a frame: closes the dialogue window past the interact range plus a margin, like the vendor.
    private void UpdateQuestHud()
    {
    }

    private void ApplyQuestLog(ServerQuestLog log)
    {
    }

    private void ApplyNpcDialogue(NpcDialogueResult dialogue)
    {
    }
}
