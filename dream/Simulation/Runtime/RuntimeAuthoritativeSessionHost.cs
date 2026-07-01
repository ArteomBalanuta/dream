using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;


public sealed class RuntimeAuthoritativeSessionHost : ITickDrivenAuthoritativeSessionHost, IDisposable
{
    public RuntimeAuthoritativeSessionHost(WorldSessionRuntime runtime)
    {
        Runtime = runtime;
    }

    public WorldSessionRuntime Runtime { get; }

    public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedSessionId);
        PlayerSessionId sessionId = new(requestedSessionId);

        if (Runtime.Sessions.TryGet(sessionId, out ConnectedPlayer? existing) && existing != null)
        {
            return new JoinAcceptedMessage(
                existing.SessionId.Value,
                existing.ActorId,
                Runtime.CurrentServerTick,
                Runtime.ExportWorldSnapshot(),
                BuildJoinedPlayerRoster());
        }

        ConnectedPlayer player = Runtime.AttachRemoteSession(sessionId, displayName);
        return new JoinAcceptedMessage(
            player.SessionId.Value,
            player.ActorId,
            Runtime.CurrentServerTick,
            Runtime.ExportWorldSnapshot(),
            BuildJoinedPlayerRoster());
    }

    public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
    {
        return Runtime.ProcessAuthoritativeAction(message);
    }

    public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
    {
        return Runtime.ExportServerSnapshotEnvelope(includeWorldSnapshot);
    }

    public void AdvanceTick(
        ulong serverTick,
        float deltaTime,
        IReadOnlyList<PlayerCommandFrame> commandFrames,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true)
    {
        PlayerCommandFrame? commandFrame = commandFrames
            .FirstOrDefault(frame => frame.SessionId == ResolveTickControlledSessionId());
        Runtime.AdvanceAuthoritativeTick(
            serverTick,
            deltaTime,
            commandFrame,
            playerTuning,
            allowGameplaySimulation);

        foreach (PlayerCommandFrame secondaryCommand in commandFrames.Where(frame => frame.SessionId != ResolveTickControlledSessionId()))
        {
            Runtime.AdvanceSecondaryRemoteSession(secondaryCommand, deltaTime, playerTuning);
        }
    }

    public void Dispose()
    {
        Runtime.Dispose();
    }

    public static RuntimeAuthoritativeSessionHost CreateDefault()
    {
        SceneEventHub events = new();
        WorldSessionRuntime runtime = new(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            events,
            loadVisualAssets: false);
        return new RuntimeAuthoritativeSessionHost(runtime);
    }

    private PlayerSessionId ResolveTickControlledSessionId()
    {
        return Runtime.PrimaryRemoteSessionId ?? Runtime.LocalPlayerSessionId;
    }

    private IReadOnlyList<PlayerRosterEntry> BuildJoinedPlayerRoster()
    {
        return Runtime.Sessions.Players
            .Where(player => !player.IsLocal)
            .Select(player => new PlayerRosterEntry(
                player.SessionId.Value,
                player.ActorId,
                player.DisplayName))
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SessionId, StringComparer.Ordinal)
            .ToArray();
    }
}
