using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using OpenTK.Mathematics;

namespace Dream.Simulation.Runtime;

// Parties (M5 workstream A). Contract stubs: the actions are refused and no party exists until
// workstream A fills this file in.
public sealed partial class WorldSessionRuntime
{
    private AuthoritativeActionResult ProcessPartyInviteAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        PartyInviteIntent intent)
        => CreateRejectedActionResult(message, "Parties are not available.");

    private AuthoritativeActionResult ProcessPartyRespondAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        PartyRespondIntent intent)
        => CreateRejectedActionResult(message, "Parties are not available.");

    private AuthoritativeActionResult ProcessPartyLeaveAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        PartyLeaveIntent intent)
        => CreateRejectedActionResult(message, "Parties are not available.");

    // On change, and every 32 ticks while parties exist, queues ServerPartyState for the members.
    private void StepPartyRefresh(ulong tick)
    {
    }

    private void LeavePartyOnDetach(PlayerSessionId session, string characterKey)
    {
    }

    // The two character keys (ordinal) are in one party.
    internal bool AreInSameParty(string characterKeyA, string characterKeyB) => false;

    // Appends the other online members of the session's party that are spawned, alive and within the
    // sharing range (40 m) of the position. False when there are none.
    internal bool TryGetNearbyPartyMembers(PlayerSessionId member, Vector3 at, List<PlayerSessionId> into) => false;

    // Appends the sessions of the party the session belongs to, itself included, in join order. False
    // when the session is in none.
    internal bool CopyPartyMemberSessions(PlayerSessionId session, List<PlayerSessionId> into) => false;
}
