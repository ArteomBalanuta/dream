using System.Diagnostics.CodeAnalysis;
using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// Command acceptance: accepted and starved command frames, deferred primary-seat use, command
// primary use, action dispatch and rejection, and the stats and interaction-mode actions.
public sealed partial class WorldSessionRuntime
{
    private readonly PhysicsSyncSystem _physicsSync = new();

    // This tick's secondary seats whose primary use triggered (after their attack-interval gate),
    // as the frames they simulated, in ordinal session-id order. Reused every tick.
    private readonly List<PlayerCommandFrame> _triggeredSecondaryUseFrames = [];
    // A primary-seat primary use that triggered this tick but was left for the caller to resolve
    // in its ordinal pass with every other seat (see AdvanceAuthoritativeTick's deferPrimarySeatUse).
    private bool _hasDeferredPrimarySeatUse;
    private PlayerCommandFrame _deferredPrimarySeatUseFrame;

    // The primary seat's command frame whose primary use triggered in the last tick advanced with
    // deferPrimarySeatUse, once. The caller resolves it with TryProcessCommandPrimaryUse in the same
    // ordinal session-id pass as the secondary seats.
    public bool TryTakeDeferredPrimarySeatUse(out PlayerCommandFrame commandFrame)
    {
        commandFrame = _deferredPrimarySeatUseFrame;
        bool taken = _hasDeferredPrimarySeatUse;
        _hasDeferredPrimarySeatUse = false;
        return taken;
    }

    // The secondary seats whose primary use triggered in the last tick, as the frames they simulated,
    // in ordinal session-id order. Each seat contributes at most one use per tick, through the same
    // attack-interval repeat gate as the primary seat. The caller resolves them with
    // TryProcessCommandPrimaryUse in its ordinal pass, merged with the primary seat's.
    public IReadOnlyList<PlayerCommandFrame> TriggeredSecondaryUseFrames => _triggeredSecondaryUseFrames;

    // The idle form of a starved seat's last command: no movement or one-shot inputs, no look
    // deltas and no primary use; the facing and the requested mode are kept.
    public static PlayerCommandFrame CreateStarvedCommandFrame(PlayerCommandFrame lastCommand)
    {
        return new PlayerCommandFrame(
            lastCommand.SessionId,
            lastCommand.Sequence,
            lastCommand.RequestedMode,
            PlayerMoveIntent.None,
            new PlayerLookIntent(0f, 0f, lastCommand.LookIntent.Facing),
            PrimaryUsePressed: false);
    }

    private WorldSessionUpdateResult UpdateAcceptedCommand(
        float delta,
        PlayerCommandFrame commandFrame,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation,
        bool deferPrimarySeatUse)
    {
        _hasDeferredPrimarySeatUse = false;
        _triggeredSecondaryUseFrames.Clear();
        if (!Player.Combatant.Health.IsAlive)
        {
            // Wave I: a dead seat does not move, jump or use; gravity, facing and the requested mode stay.
            commandFrame = CreateStarvedCommandFrame(commandFrame);
        }

        Player.Combatant.AdvanceCombat(delta);
        PlayerInteractionCommand interaction = commandFrame.ToInteractionCommand();
        ApplyInteractionModeSelection(commandFrame.SessionId, interaction);
        ApplyCommandFacing(commandFrame);
        PlayerSimulationInput playerInput = CreatePlayerSimulationInput(commandFrame);
        AuthoritativeActionResult? actionResult = null;

        bool primaryUseTriggered = Player.ConsumePrimaryUsePressed(
            interaction,
            Player.Combatant.DerivedStats.AttackIntervalSeconds,
            delta);

        if (primaryUseTriggered && deferPrimarySeatUse)
        {
            // Resolved by the caller after the tick, ordered with the secondary seats, so holding
            // the primary seat (joining first) no longer wins every same-tick race.
            _deferredPrimarySeatUseFrame = commandFrame;
            _hasDeferredPrimarySeatUse = true;
        }
        else if (primaryUseTriggered
            && TryProcessCommandPrimaryUse(commandFrame, out AuthoritativeActionResult? primaryUseResult))
        {
            actionResult = primaryUseResult;
        }

        if (!Physics.IsPlayerBodyParked)
        {
            Player.Update(
                delta,
                playerInput,
                playerTuning);
        }

        foreach (RemoteSessionPlayerState secondary in _orderedSecondaryRemotePlayers)
        {
            secondary.AdvanceCombat(delta);
            secondary.Simulate(delta, playerTuning);
            if (secondary.PrimaryUseTriggered)
            {
                _triggeredSecondaryUseFrames.Add(secondary.SimulatedCommand);
            }
        }

        Physics.StepSimulationFixed(delta);
        if (!Physics.IsPlayerBodyParked)
        {
            Player.SyncCameraFromBody(playerTuning);
        }

        foreach (RemoteSessionPlayerState secondary in _orderedSecondaryRemotePlayers)
        {
            secondary.SyncAfterPhysicsStep(playerTuning);
        }

        if (allowGameplaySimulation)
        {
            // Filled once, before the enemies act, so a player killed this tick is still separated.
            FillAuthoritativePlayerTargets(_tickPlayerTargets, spawnedAndAliveOnly: true);
            FillEnemyTargets(_tickPlayerTargets, _enemyTargets);
            _enemies.UpdateAll(delta, _enemyTargets, _events, Map);
            _enemyTargets.Clear();
            _enemies.PruneInactive();
            EnsureWolfExists();
            ResolvePlayerEnemySeparation(_tickPlayerTargets);
            // Do not keep a detached (disposed) secondary referenced until the next tick.
            _tickPlayerTargets.Clear();
            AdvanceRespawns();
        }

        PhysicsSyncResult syncResult = _physicsSync.SyncDynamicMapBodies(Map, Physics.DynamicMapCubeBodies);
        IReadOnlyList<PhysicsCubeMoveDelta> movedCubes = syncResult.MovedCubes;
        for (int i = 0; i < movedCubes.Count; i++)
        {
            PhysicsCubeMoveDelta movedCube = movedCubes[i];
            QueueWorldMutation(new WorldMutationDelta(
                WorldMutationKind.CubeMoved,
                movedCube.SourceCellPosition,
                movedCube.TargetCellPosition,
                movedCube.CenterPosition,
                ActorId: _worldObjects.TryGet(movedCube.Cube)?.Actor.Id));
        }

        return new WorldSessionUpdateResult(primaryUseTriggered, actionResult);
    }

    // A client-sent action: its sequence must be newer than every action already accepted for the
    // session, so a replayed or reordered action is rejected.
    public AuthoritativeActionResult ProcessAuthoritativeAction(PlayerActionMessage message)
    {
        return ProcessAuthoritativeAction(message, fromAcceptedCommand: false);
    }

    // Resolves the primary use the server synthesized from an already-accepted command frame (the
    // attack-interval repeat gate triggered it this tick). Returns false when the seat's mode makes
    // no use (for example Building with no valid placement target).
    //
    // The command frame already passed the command-sequence gate when it arrived, so the use does not
    // go through the client action-sequence gate again. A held button repeats from the seat's last
    // command, so one command frame can trigger several uses on different ticks (a short attack
    // interval with a stalled client, or a command that lands on the same sequence as a client action
    // just accepted); every one of them is legitimate. The result still carries the command's
    // sequence, which clients only advance past, and the seeded rolls stay distinct because they are
    // keyed by the tick as well as the sequence.
    public bool TryProcessCommandPrimaryUse(
        PlayerCommandFrame commandFrame,
        [NotNullWhen(true)] out AuthoritativeActionResult? result)
    {
        PlayerActionMessage? actionMessage = TryCreateCommandPrimaryUseActionMessage(commandFrame);
        if (actionMessage == null)
        {
            result = null;
            return false;
        }

        result = ProcessAuthoritativeAction(actionMessage, fromAcceptedCommand: true);
        return true;
    }

    private AuthoritativeActionResult ProcessAuthoritativeAction(PlayerActionMessage message, bool fromAcceptedCommand)
    {
        if (!TryResolveActionContext(message.SessionId, out AuthoritativeSessionActionContext? context))
        {
            return CreateRejectedActionResult(message, "Unknown player session.");
        }

        ArgumentNullException.ThrowIfNull(context);

        if (!context.Player.IsSpawned)
        {
            return CreateRejectedActionResult(message, "Player session is not spawned in the authoritative world.");
        }

        // A synthesized use still advances the session's accepted action sequence (as before), so a
        // client action at or below a command sequence the server already used stays stale; it is
        // only never rejected by it.
        bool sequenceAccepted = _sessions.TryAcceptAction(message.SessionId, message.ActionSequence, out _);
        if (!sequenceAccepted && !fromAcceptedCommand)
        {
            return CreateRejectedActionResult(message, "Action sequence is stale or has already been accepted.");
        }

        // Wave I: a dead player neither builds nor fights (inventory, stats, material and mode stay
        // allowed). After the sequence step, so the rejected action is spent like any other; nothing
        // else changes and no write is queued.
        // M3: a dead player does not pick up, talk or trade either.
        if (!context.Combatant.Health.IsAlive
            && message.Payload is BuildUseIntent or CombatUseIntent
                or GroundPickupIntent or NpcInteractIntent or VendorBuyIntent or VendorSellIntent)
        {
            return CreateRejectedActionResult(message, "Action rejected because the player is dead.");
        }

        // Attacking ends spawn protection, so it cannot be used to fight safely.
        if (message.Payload is CombatUseIntent && context.InteractionMode == PlayerInteractionMode.Combat)
        {
            context.Player.ProtectedThroughTick = 0;
        }

        return message.Payload switch
        {
            BuildUseIntent buildIntent when context.SupportsBuildActions
                => ProcessBuildUseAction(
                    message,
                    buildIntent,
                    context.InteractionMode,
                    context.CombatRayOrigin),
            BuildUseIntent
                => CreateRejectedActionResult(
                    message,
                    "This player session is not yet attached to authoritative build ownership."),
            CombatUseIntent combatIntent when context.SupportsCombatActions
                => ProcessCombatUseAction(
                    message,
                    ResolveAuthoritativeCombatIntent(context, combatIntent),
                    context.InteractionMode,
                    context.Combatant),
            InventoryEquipIntent equipIntent
                => ProcessInventoryEquipAction(message, context.Player, equipIntent),
            InventoryUnequipIntent unequipIntent
                => ProcessInventoryUnequipAction(message, context.Player, unequipIntent),
            InventoryMoveIntent moveIntent
                => ProcessInventoryMoveAction(message, context.Player, moveIntent),
            InventoryDestroyIntent destroyIntent
                => ProcessInventoryDestroyAction(message, context.Player, destroyIntent),
            BuildMaterialSelectIntent materialIntent
                => ProcessBuildMaterialSelectAction(message, context.Player, materialIntent),
            PlayerStatsUpdateIntent statsIntent
                => ProcessPlayerStatsUpdateAction(message, context.Combatant, statsIntent),
            PlayerStatSpendIntent spendIntent
                => ProcessPlayerStatSpendAction(message, context.Combatant, spendIntent),
            GroundPickupIntent pickupIntent
                => ProcessGroundPickupAction(message, context.Player, pickupIntent),
            NpcInteractIntent interactIntent
                => ProcessNpcInteractAction(message, context.Player, interactIntent),
            VendorBuyIntent buyIntent
                => ProcessVendorBuyAction(message, context.Player, buyIntent),
            VendorSellIntent sellIntent
                => ProcessVendorSellAction(message, context.Player, sellIntent),
            CombatUseIntent
                => CreateRejectedActionResult(
                    message,
                    "This player session is not yet attached to full authoritative combat ownership."),
            _ => CreateRejectedActionResult(message, $"Unsupported action payload '{message.Payload.GetType().Name}'.")
        };
    }

    private void ApplyInteractionModeSelection(PlayerSessionId sessionId, PlayerInteractionCommand command)
    {
        if (command.SelectBuildingModePressed)
        {
            SetSessionInteractionMode(sessionId, PlayerInteractionMode.Building);
        }
        else if (command.SelectCombatModePressed)
        {
            SetSessionInteractionMode(sessionId, PlayerInteractionMode.Combat);
        }
    }

    private PlayerSimulationInput CreatePlayerSimulationInput(PlayerCommandFrame commandFrame)
    {
        return new PlayerSimulationInput(
            PlayerMovementResolver.CreateMovementCommand(Player.Camera, Player.IsNoclipEnabled, commandFrame.MoveIntent),
            new Vector2(commandFrame.LookIntent.YawDelta, commandFrame.LookIntent.PitchDelta),
            AllowGameplayInput: true,
            AllowLook: true);
    }

    private void ApplyCommandFacing(PlayerCommandFrame commandFrame)
    {
        if (commandFrame.LookIntent.Facing.HasValue)
        {
            Player.SetFacing(commandFrame.LookIntent.Facing.Value);
        }
    }

    private PlayerActionMessage? TryCreateCommandPrimaryUseActionMessage(PlayerCommandFrame commandFrame)
    {
        if (!TryResolveActionContext(commandFrame.SessionId, out AuthoritativeSessionActionContext? context) ||
            context == null)
        {
            return null;
        }

        return context.InteractionMode switch
        {
            PlayerInteractionMode.Building => PlayerInteraction.TryCreateBuildUseRequest(
                    context.CombatRayOrigin,
                    context.CombatRayDirection,
                    out BuildUseRequest buildUseRequest)
                ? new PlayerActionMessage(
                    commandFrame.SessionId,
                    commandFrame.Sequence,
                    new BuildUseIntent(buildUseRequest.PlacementTarget))
                : null,
            PlayerInteractionMode.Combat => new PlayerActionMessage(
                commandFrame.SessionId,
                commandFrame.Sequence,
                new CombatUseIntent(context.CombatRayOrigin, context.CombatRayDirection)),
            _ => null
        };
    }

    private AuthoritativeActionResult ProcessPlayerStatsUpdateAction(
        PlayerActionMessage message,
        PlayerCombatant combatant,
        PlayerStatsUpdateIntent intent)
    {
        // Base stats drive the use cadence (attack interval) and damage, so a client may only pick
        // what the stat editor allows; see PlayerCombatStats.TryValidateBaseStats.
        if (!PlayerCombatStats.TryValidateBaseStats(intent.Stats, out string? invalidReason))
        {
            return CreateRejectedActionResult(message, $"Stats update rejected: {invalidReason}");
        }

        combatant.ApplyBaseStats(intent.Stats.ToCombatStats());
        PersistPlayerProfile(message.SessionId, combatant);

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new PlayerStatsUpdateResult(PlayerCombatStatsSnapshot.From(combatant.BaseStats)));
    }

    // Stat points. The context's combatant is the sending session's own character, so a client can only
    // spend for itself. The budget comes from the level and the current base stats (PlayerStatRules).
    private AuthoritativeActionResult ProcessPlayerStatSpendAction(
        PlayerActionMessage message,
        PlayerCombatant combatant,
        PlayerStatSpendIntent intent)
    {
        if (!PlayerStatRules.TryValidateSpend(
                combatant.Level,
                PlayerCombatStatsSnapshot.From(combatant.BaseStats),
                intent.Stat,
                intent.Points,
                out PlayerCombatStatsSnapshot newStats,
                out string? invalidReason))
        {
            return CreateRejectedActionResult(message, $"Stat spend rejected: {invalidReason}");
        }

        combatant.ApplyBaseStats(newStats.ToCombatStats());
        PersistPlayerProfile(message.SessionId, combatant);

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new PlayerStatsUpdateResult(PlayerCombatStatsSnapshot.From(combatant.BaseStats)));
    }

    private AuthoritativeActionResult CreateRejectedActionResult(PlayerActionMessage message, string reason)
    {
        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: false,
            Payload: null,
            Rejection: new RejectedUseDelta(reason));
    }

    private static PlayerCommandFrame CreateIdleCommandFrame(PlayerSessionId sessionId, ulong sequence)
    {
        return new PlayerCommandFrame(
            sessionId,
            sequence,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
            new PlayerMoveIntent(0f, 0f, 0f),
            new PlayerLookIntent(0f, 0f),
            PrimaryUsePressed: false);
    }
}
