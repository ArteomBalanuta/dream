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
    // Ordinal by session id, then by sequence, so frames from different sessions are applied in
    // the same order on every process regardless of how the caller collected them.
    private static readonly Comparison<PlayerCommandFrame> CompareBySessionIdThenSequence =
        static (left, right) =>
        {
            int bySession = string.CompareOrdinal(left.SessionId.Value, right.SessionId.Value);
            return bySession != 0 ? bySession : left.Sequence.CompareTo(right.Sequence);
        };

    private readonly IDisposable? _ownedDependency;

    // Tick scratch: this tick's secondary command frames in canonical order. Reused every tick.
    private readonly List<PlayerCommandFrame> _orderedSecondaryCommands = [];

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
            // A session that left stays registered (DetachRemoteSession only despawns it), so a
            // reconnect lands here. It must go through AttachRemoteSession like a first join: that
            // recreates the secondary player DetachRemoteSession disposed, re-seats a returning former
            // primary (or gives it its own seat when the primary seat was taken) and invalidates the
            // cached player targets. For a session that is still attached it creates nothing.
            bool returning = !existing.IsSpawned;
            if (existing.IsLocal)
            {
                existing.UpdateDisplayName(normalizedDisplayName);
                existing.MarkSpawned();
            }
            else
            {
                existing = Runtime.AttachRemoteSession(sessionId, normalizedDisplayName);
                if (returning)
                {
                    // DetachRemoteSession saved the profile; a recreated player starts from it.
                    Runtime.RestorePersistedPlayerState(sessionId, existing.DisplayName);
                }
            }

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
        ArgumentNullException.ThrowIfNull(commandFrames);
        ArgumentNullException.ThrowIfNull(queuedActions);

        // Resolved once, before the runtime advances, like the snapshot the LINQ version took.
        // With no frame for the controlled seat the runtime still receives a default frame, as before.
        PlayerSessionId controlledSessionId = ResolveTickControlledSessionId();
        PlayerCommandFrame commandFrame = default;
        for (int i = 0; i < commandFrames.Count; i++)
        {
            if (commandFrames[i].SessionId == controlledSessionId)
            {
                commandFrame = commandFrames[i];
                break;
            }
        }

        // Secondary commands and their primary-use actions run in ordinal session-id order, so two
        // players hitting the same target in one tick resolve the same way on every server run.
        _orderedSecondaryCommands.Clear();
        for (int i = 0; i < commandFrames.Count; i++)
        {
            if (commandFrames[i].SessionId != controlledSessionId)
            {
                _orderedSecondaryCommands.Add(commandFrames[i]);
            }
        }

        if (_orderedSecondaryCommands.Count > 1)
        {
            _orderedSecondaryCommands.Sort(CompareBySessionIdThenSequence);
        }

        // Record secondary commands first so this tick's physics step simulates them, exactly
        // like the primary seat's command.
        for (int i = 0; i < _orderedSecondaryCommands.Count; i++)
        {
            Runtime.AdvanceSecondaryRemoteSession(_orderedSecondaryCommands[i], deltaTime, playerTuning);
        }

        // The primary seat's primary use is deferred, not resolved inline before everyone else's,
        // so the session that joined first (and holds the seat) does not win every same-tick race.
        Runtime.AdvanceAuthoritativeTick(
            serverTick,
            deltaTime,
            commandFrame,
            playerTuning,
            allowGameplaySimulation,
            deferPrimarySeatUse: true);

        // One ordinal pass over every seat's primary use: the primary seat's (if it triggered,
        // after its attack-interval gate) merged into the already sorted secondary frames.
        bool primarySeatUsePending = Runtime.TryTakeDeferredPrimarySeatUse(out PlayerCommandFrame primarySeatUseFrame);
        List<AuthoritativeActionResult>? synthesizedCommandActions = null;
        int secondaryIndex = 0;
        while (primarySeatUsePending || secondaryIndex < _orderedSecondaryCommands.Count)
        {
            PlayerCommandFrame useCommand;
            if (primarySeatUsePending
                && (secondaryIndex >= _orderedSecondaryCommands.Count
                    || CompareBySessionIdThenSequence(primarySeatUseFrame, _orderedSecondaryCommands[secondaryIndex]) <= 0))
            {
                useCommand = primarySeatUseFrame;
                primarySeatUsePending = false;
            }
            else
            {
                useCommand = _orderedSecondaryCommands[secondaryIndex++];
                if (!useCommand.PrimaryUsePressed)
                {
                    continue;
                }
            }

            PlayerActionMessage? actionMessage = Runtime.TryCreateCommandPrimaryUseActionMessage(useCommand);
            if (actionMessage != null)
            {
                synthesizedCommandActions ??= [];
                synthesizedCommandActions.Add(Runtime.ProcessAuthoritativeAction(actionMessage));
            }
        }

        // AdvanceAuthoritativeTick already set the runtime's tick, so an empty queue needs no call.
        IReadOnlyList<AuthoritativeActionResult> queuedActionResults = queuedActions.Count == 0
            ? Array.Empty<AuthoritativeActionResult>()
            : Runtime.ProcessQueuedAuthoritativeActions(serverTick, queuedActions);

        if (synthesizedCommandActions == null)
        {
            return queuedActionResults;
        }

        if (queuedActionResults.Count == 0)
        {
            return synthesizedCommandActions;
        }

        AuthoritativeActionResult[] combined = new AuthoritativeActionResult[synthesizedCommandActions.Count + queuedActionResults.Count];
        synthesizedCommandActions.CopyTo(combined);
        for (int i = 0; i < queuedActionResults.Count; i++)
        {
            combined[synthesizedCommandActions.Count + i] = queuedActionResults[i];
        }

        return combined;
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
