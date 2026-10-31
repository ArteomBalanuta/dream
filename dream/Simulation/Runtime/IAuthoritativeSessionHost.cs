using Dream.Multiplayer.Protocol;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

public interface IAuthoritativeSessionHost
{
    JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName);

    // Decides, from memory only, whether the account may play the named character. The default
    // checks nothing (a host without accounts).
    Dream.Simulation.Characters.CharacterAccessDecision AuthorizeJoin(
        string requestedSessionId,
        string displayName,
        Dream.Simulation.Characters.AccountKey? account)
        => Dream.Simulation.Characters.CharacterAccessDecision.Unchecked(displayName.Trim());

    // Joins with an access decision made by AuthorizeJoin. The default ignores it.
    JoinAcceptedMessage JoinSession(
        string requestedSessionId,
        string displayName,
        in Dream.Simulation.Characters.CharacterAccessDecision access)
        => JoinSession(requestedSessionId, displayName);

    AuthoritativeActionResult ProcessAction(PlayerActionMessage message);

    ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false);
}
