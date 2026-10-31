using OpenTK.Mathematics;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Rules;
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

    // The join-accepted descriptor, cached for the runtime's current WorldDescriptor.
    private WorldDescriptorWire? _worldWire;
    private WorldDescriptor? _worldWireSource;

    // Tick scratch: this tick's secondary command frames in canonical order. Reused every tick.
    private readonly List<PlayerCommandFrame> _orderedSecondaryCommands = [];

    public RuntimeAuthoritativeSessionHost(WorldSessionRuntime runtime)
        : this(runtime, null)
    {
    }

    public RuntimeAuthoritativeSessionHost(WorldSessionRuntime runtime, IDisposable? ownedDependency)
        : this(runtime, ownedDependency, null)
    {
    }

    // terrainPayload: the stored world transfer when the caller already built it (bootstrap does);
    // null builds it here. If construction throws, the runtime is disposed (the owned dependency is not).
    public RuntimeAuthoritativeSessionHost(
        WorldSessionRuntime runtime, IDisposable? ownedDependency, TerrainTransferPayload? terrainPayload)
    {
        Runtime = runtime;
        _ownedDependency = ownedDependency;
        // Built once per host: every join of a stored world shares the same chunks and manifest.
        // A runtime hosts one world for its lifetime, so the payload never changes.
        try
        {
            _terrainPayload = terrainPayload ??
                (runtime.World.MapSource == WorldMapSource.Stored && runtime.StoredTerrain != null
                    ? TerrainTransferPayload.Create(runtime.World, runtime.StoredTerrain)
                    : null);
        }
        catch
        {
            runtime.DisposeWithoutFinalCheckpoint();
            throw;
        }
    }

    private readonly TerrainTransferPayload? _terrainPayload;

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
            // A session that left stays registered (DetachRemoteSession only despawns it) while it
            // is among the registry's most recently left sessions
            // (PlayerSessionRegistry.MaxDepartedSessions), so a reconnect lands here; an older one
            // joins as new. It must go through AttachRemoteSession like a first join: that
            // recreates the secondary player DetachRemoteSession disposed, re-seats a returning
            // former primary (or gives it its own seat when the primary seat was taken) and
            // invalidates the cached player targets. For a session that is still attached it
            // creates nothing.
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
                InitialWorldMutationSequence: Runtime.LastWorldMutationSequence,
                WorldClock: Runtime.WorldClock.CaptureAnchor(Runtime.CurrentServerTick),
                World: GetWorldWire(),
                Terrain: _terrainPayload?.Manifest)
            {
                TerrainPayload = _terrainPayload,
            };
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
            InitialWorldMutationSequence: Runtime.LastWorldMutationSequence,
            WorldClock: Runtime.WorldClock.CaptureAnchor(Runtime.CurrentServerTick),
            World: GetWorldWire(),
            Terrain: _terrainPayload?.Manifest)
        {
            TerrainPayload = _terrainPayload,
        };
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

        // Secondary commands, and every seat's primary-use actions, run in ordinal session-id order,
        // so two players hitting the same target in one tick resolve the same way on every server run.
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

        // One ordinal pass over every seat's primary use. Every seat's use went through the same
        // attack-interval repeat gate in the runtime tick, evaluated each tick from its last command,
        // so a held button repeats at the same cadence on every seat instead of once per received
        // command. The primary seat's (if it triggered) merges into the already ordered secondaries.
        bool primarySeatUsePending = Runtime.TryTakeDeferredPrimarySeatUse(out PlayerCommandFrame primarySeatUseFrame);
        IReadOnlyList<PlayerCommandFrame> secondaryUses = Runtime.TriggeredSecondaryUseFrames;
        List<AuthoritativeActionResult>? synthesizedCommandActions = null;
        int secondaryIndex = 0;
        while (primarySeatUsePending || secondaryIndex < secondaryUses.Count)
        {
            PlayerCommandFrame useCommand;
            if (primarySeatUsePending
                && (secondaryIndex >= secondaryUses.Count
                    || CompareBySessionIdThenSequence(primarySeatUseFrame, secondaryUses[secondaryIndex]) <= 0))
            {
                useCommand = primarySeatUseFrame;
                primarySeatUsePending = false;
            }
            else
            {
                useCommand = secondaryUses[secondaryIndex++];
            }

            // Server-synthesized from an already-accepted command, so not through the client
            // action-sequence gate: a held use repeating from one command frame is not stale.
            if (Runtime.TryProcessCommandPrimaryUse(useCommand, out AuthoritativeActionResult? useResult))
            {
                synthesizedCommandActions ??= [];
                synthesizedCommandActions.Add(useResult);
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

    // The runtime rolls with the persisted world's seed when the ports carry one; a host with no
    // persistence (tests, ephemeral hosts) keeps the deterministic DefaultSimulationSeed. The server
    // builds the ports from its SQLite bundle in ServerBootstrap.CreateRuntimeHost.
    public static RuntimeAuthoritativeSessionHost CreateDefault(RuntimePersistencePorts? persistence = null)
    {
        SceneEventHub events = new();
        WorldSessionRuntime runtime = new(
            world: persistence?.Descriptor ?? WorldDescriptor.Default,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            events,
            playerProfiles: persistence?.PlayerProfiles,
            worldPersistence: persistence?.World,
            combatFormulaService: persistence?.FormulaService ?? CombatFormulaService.Default,
            checkpointWriter: persistence?.CheckpointWriter,
            persistenceWrites: persistence?.PersistenceWrites,
            simulationSeed: persistence?.SimulationSeed ?? WorldSessionRuntime.DefaultSimulationSeed,
            storedTerrain: persistence?.StoredTerrain,
            accounts: persistence?.Accounts);
        return new RuntimeAuthoritativeSessionHost(runtime, persistence?.Owner, persistence?.TerrainPayload);
    }

    // The wire form of the runtime's world, built once per descriptor (a join is not a tick, but
    // the descriptor only changes when the map is rebuilt).
    private WorldDescriptorWire GetWorldWire()
    {
        WorldDescriptor world = Runtime.World;
        if (_worldWire == null || !ReferenceEquals(_worldWireSource, world))
        {
            _worldWire = WorldDescriptorWire.From(world);
            _worldWireSource = world;
        }

        return _worldWire;
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
