using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;

namespace Dream.Simulation.Runtime;

// One server message for one session; DedicatedServerHost turns each into a reply for the session's
// connection.
internal readonly record struct SessionServerMessage(PlayerSessionId SessionId, IServerToClientMessage Message);

// Multiplayer seams (M5, M6 and M8 contract commit): the generic server push queue, the per tick step
// and the detach hook. The party, trade, quest and owner progress features live in their own partials
// and plug into StepMultiplayerSystems and OnSessionDetachedMultiplayer.
//
// Session messages are never action results: kills, trades and the like sit inside the tick golden,
// which hashes every result, so they reach the owner through this queue after the tick, like vitals.
public sealed partial class WorldSessionRuntime
{
    // A host that never drains cannot grow the queue past this; the oldest message goes first.
    private const int MaxQueuedSessionMessages = 1024;

    private readonly List<SessionServerMessage> _sessionMessages = [];

    // M5: whether an online party mate of a cube's owner may damage it. Off by default; the dedicated
    // bootstrap sets it from DREAM_PARTY_CUBES.
    public bool PartyCubeAccess { get; set; }

    // M8: whether actor snapshots stop carrying other players' experience and stats (the owner gets
    // them as ServerPlayerProgress). Off by default; the dedicated bootstrap turns it on.
    public bool OwnerOnlyProgress { get; set; }

    internal bool HasPendingSessionMessages => _sessionMessages.Count > 0;

    internal void QueueSessionMessage(PlayerSessionId session, IServerToClientMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_sessionMessages.Count >= MaxQueuedSessionMessages)
        {
            _sessionMessages.RemoveAt(0);
        }

        _sessionMessages.Add(new SessionServerMessage(session, message));
    }

    // Moves the queued messages into into, oldest first. A message of a session isClaimed rejects (no
    // connection yet) stays queued for a later drain.
    internal void DrainSessionMessagesInto(List<SessionServerMessage> into, Func<PlayerSessionId, bool>? isClaimed = null)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (_sessionMessages.Count == 0)
        {
            return;
        }

        if (isClaimed == null)
        {
            into.AddRange(_sessionMessages);
            _sessionMessages.Clear();
            return;
        }

        int kept = 0;
        for (int i = 0; i < _sessionMessages.Count; i++)
        {
            SessionServerMessage entry = _sessionMessages[i];
            if (isClaimed(entry.SessionId))
            {
                into.Add(entry);
            }
            else
            {
                _sessionMessages[kept++] = entry;
            }
        }

        _sessionMessages.RemoveRange(kept, _sessionMessages.Count - kept);
    }

    // Called once a tick (gameplay ticks only); each callee only reads a count when idle.
    private void StepMultiplayerSystems(ulong tick)
    {
        StepTrades(tick);
        StepPartyRefresh(tick);
        StepOwnerProgress(tick);
        StepPresence(tick);
    }

    // Called from the detach path, before MarkDespawned. Also drops the queued messages of the session,
    // because a session id can be reused.
    private void OnSessionDetachedMultiplayer(PlayerSessionId session, string characterKey)
    {
        CancelTradeOnDetach(session, characterKey);
        LeavePartyOnDetach(session, characterKey);
        ForgetOwnerProgress(session);
        ReleasePresenceOnDetach(characterKey);
        _sessionMessages.RemoveAll(entry => entry.SessionId == session);
    }
}
