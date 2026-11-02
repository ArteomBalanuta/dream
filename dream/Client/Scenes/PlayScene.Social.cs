using Dream.Multiplayer.Protocol;

namespace Dream.Client.Scenes;

// Party and trade on the client (M5 workstream A). Contract stubs: the messages are accepted and ignored
// until workstream A fills them in.
public partial class PlayScene
{
    // Once a frame: closes the trade window when it no longer fits (died, lost the session).
    private void UpdatePartyTradeHud()
    {
    }

    private void ApplyPartyState(ServerPartyState state)
    {
    }

    private void ApplyTradeState(ServerTradeState state)
    {
    }
}
