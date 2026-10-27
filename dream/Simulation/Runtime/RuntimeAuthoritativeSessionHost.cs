using OpenTK.Mathematics;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Rules;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;


public sealed class RuntimeAuthoritativeSessionHost : ITickDrivenAuthoritativeSessionHost, IWorldResyncSource, IDisposable
{
    private readonly IDisposable? _ownedDependency;

    public RuntimeAuthoritativeSessionHost(WorldSessionRuntime runtime)
        : this(runtime, null)
    {
    }

    public RuntimeAuthoritativeSessionHost(WorldSessionRuntime runtime, IDisposable? ownedDependency)
    {
        Runtime = runtime;
        _ownedDependency = ownedDependency;
    }

    public WorldSessionRuntime Runtime { get; }

    public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedSessionId);
        PlayerSessionId sessionId = new(requestedSessionId);
        string normalizedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? requestedSessionId
            : displayName.Trim();

        if (Runtime.Sessions.TryGet(sessionId, out ConnectedPlayer? existing) && existing != null)
        {
            existing.UpdateDisplayName(normalizedDisplayName);
            existing.MarkSpawned();
            existing.ResetAcceptedSequences();
            PlayerInventorySnapshot? existingInventory = Runtime.TryCaptureInventorySnapshot(existing.SessionId);
            JoinAcceptedMessage existingAccepted = new(
                existing.SessionId.Value,
                existing.ActorId,
                Runtime.CurrentServerTick,
                InitialWorld: Runtime.ExportWorldSnapshot(),
                Roster: BuildJoinedPlayerRoster(),
                Inventory: existingInventory,
                InitialActors: Runtime.CaptureActorSnapshots(),
                InitialWorldMutationSequence: Runtime.LastWorldMutationSequence);
            LogJoinAccepted("existing", existingAccepted);
            return existingAccepted;
        }

        bool duplicateNameInUse = Runtime.Sessions.Players.Any(player =>
            !player.IsLocal &&
            player.IsSpawned &&
            player.SessionId != sessionId &&
            string.Equals(player.DisplayName, normalizedDisplayName, StringComparison.OrdinalIgnoreCase));
        if (duplicateNameInUse)
        {
            NetworkDiagnosticsLog.Write(
                "authority.join.reject",
                $"session={requestedSessionId} name={normalizedDisplayName} reason=duplicate-display-name");
            throw new InvalidOperationException(
                $"Display name '{normalizedDisplayName}' is already claimed by another active player.");
        }

        ConnectedPlayer player = Runtime.AttachRemoteSession(sessionId, normalizedDisplayName);
        Runtime.RestorePersistedPlayerState(sessionId, player.DisplayName);
        PlayerInventorySnapshot? inventory = Runtime.TryCaptureInventorySnapshot(player.SessionId);
        JoinAcceptedMessage accepted = new(
            player.SessionId.Value,
            player.ActorId,
            Runtime.CurrentServerTick,
            InitialWorld: Runtime.ExportWorldSnapshot(),
            Roster: BuildJoinedPlayerRoster(),
            Inventory: inventory,
            InitialActors: Runtime.CaptureActorSnapshots(),
            InitialWorldMutationSequence: Runtime.LastWorldMutationSequence);
        LogJoinAccepted("new", accepted);
        return accepted;
    }

    public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
    {
        return Runtime.ProcessAuthoritativeAction(message);
    }

    public void ReleaseSession(string sessionId)
    {
        Runtime.DetachRemoteSession(new PlayerSessionId(sessionId));
    }

    public ServerSnapshotEnvelope ExportWorldResync()
    {
        return Runtime.ExportWorldResyncEnvelope();
    }

    public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
    {
        return Runtime.ExportServerSnapshotEnvelope(includeWorldSnapshot);
    }

    public IReadOnlyList<AuthoritativeActionResult> AdvanceTick(
        ulong serverTick,
        float deltaTime,
        IReadOnlyList<PlayerCommandFrame> commandFrames,
        IReadOnlyList<QueuedPlayerAction> queuedActions,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true)
    {
        PlayerCommandFrame? commandFrame = commandFrames
            .FirstOrDefault(frame => frame.SessionId == ResolveTickControlledSessionId());
        PlayerCommandFrame[] secondaryCommands = commandFrames
            .Where(frame => frame.SessionId != ResolveTickControlledSessionId())
            .ToArray();

        // Record secondary commands first so this tick's physics step simulates them, exactly
        // like the primary seat's command.
        foreach (PlayerCommandFrame secondaryCommand in secondaryCommands)
        {
            Runtime.AdvanceSecondaryRemoteSession(secondaryCommand, deltaTime, playerTuning);
        }

        Runtime.AdvanceAuthoritativeTick(
            serverTick,
            deltaTime,
            commandFrame,
            playerTuning,
            allowGameplaySimulation);

        List<AuthoritativeActionResult> synthesizedCommandActions = [];
        foreach (PlayerCommandFrame secondaryCommand in secondaryCommands)
        {
            if (secondaryCommand.PrimaryUsePressed)
            {
                PlayerActionMessage? actionMessage = Runtime.TryCreateCommandPrimaryUseActionMessage(secondaryCommand);
                if (actionMessage != null)
                {
                    synthesizedCommandActions.Add(Runtime.ProcessAuthoritativeAction(actionMessage));
                }
            }
        }

        IReadOnlyList<AuthoritativeActionResult> queuedActionResults = Runtime.ProcessQueuedAuthoritativeActions(
            serverTick,
            queuedActions);

        if (synthesizedCommandActions.Count == 0)
        {
            return queuedActionResults;
        }

        if (queuedActionResults.Count == 0)
        {
            return synthesizedCommandActions;
        }

        return synthesizedCommandActions
            .Concat(queuedActionResults)
            .ToArray();
    }

    public IReadOnlyList<AuthoritativeActionResult> AdvanceTick(
        ulong serverTick,
        float deltaTime,
        IReadOnlyList<PlayerCommandFrame> commandFrames,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true)
    {
        return AdvanceTick(
            serverTick,
            deltaTime,
            commandFrames,
            Array.Empty<QueuedPlayerAction>(),
            playerTuning,
            allowGameplaySimulation);
    }

    public void Dispose()
    {
        Runtime.Dispose();
        _ownedDependency?.Dispose();
    }

    public static RuntimeAuthoritativeSessionHost CreateDefault(ServerPersistenceBundle? persistence = null)
    {
        SceneEventHub events = new();
        WorldSessionRuntime runtime = new(
            floorSize: WorldDefaults.StartupFloorSize,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            events,
            loadVisualAssets: false,
            playerProfiles: persistence?.PlayerProfiles,
            worldPersistence: persistence?.World,
            combatFormulaService: persistence?.FormulaService ?? CombatFormulaService.Default,
            checkpointWriter: persistence != null ? new SqlitePersistenceCheckpointWriter(persistence.Schema) : null,
            persistenceWrites: persistence != null ? PersistenceWriteQueue.Background() : null);
        return new RuntimeAuthoritativeSessionHost(runtime, persistence);
    }

    private PlayerSessionId ResolveTickControlledSessionId()
    {
        return Runtime.PrimaryRemoteSessionId ?? Runtime.LocalPlayerSessionId;
    }

    private IReadOnlyList<PlayerRosterEntry> BuildJoinedPlayerRoster()
    {
        return Runtime.Sessions.Players
            .Where(player => !player.IsLocal && player.IsSpawned)
            .Select(player => new PlayerRosterEntry(
                player.SessionId.Value,
                player.ActorId,
                player.DisplayName))
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    private void LogJoinAccepted(string path, JoinAcceptedMessage accepted)
    {
        string primary = Runtime.PrimaryRemoteSessionId?.Value ?? "<none>";
        string sessions = string.Join(
            ", ",
            Runtime.Sessions.Players.Select(player =>
                $"{player.SessionId.Value}#{player.ActorId}/{player.DisplayName}/local={player.IsLocal}/spawned={player.IsSpawned}"));
        NetworkDiagnosticsLog.Write(
            "authority.join.accept",
            $"path={path} primary={primary} sessions=[{sessions}] {NetworkDiagnosticsLog.DescribeJoin(accepted)}");
    }
}
