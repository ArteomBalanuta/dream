using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Simulation.Runtime;

// Wave I2 (I-D12): a dead player does not move, jump, use, build or fight, on the primary and on the
// secondary seats. Inventory, stats, material select and mode change stay allowed, and none of them can
// heal. Before this wave the dead could walk, build and kill (the plan's correction 3).
public sealed class DeadPlayerActionTests
{
    private static readonly PlayerSessionId A = new("dead-a");
    private static readonly PlayerSessionId B = new("dead-b");

    // Forward, with a jump on every 20th command.
    private static PlayerCommandFrame Walk(PlayerSessionId session, ulong sequence, bool primaryUse = false) => new(
        session,
        sequence,
        new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
        new PlayerMoveIntent(1f, 0f, JumpPressed: sequence % 20 == 0),
        new PlayerLookIntent(0f, 0f, Vector3.UnitZ),
        primaryUse);

    private static Vector3 WalkFor(bool dead, bool secondary)
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        if (secondary)
        {
            runtime.AttachRemoteSession(B, B.Value);
        }

        PlayerSessionId walker = secondary ? B : A;
        ulong tick = 0;
        // Open ground (the start). Another cell may be blocked by terrain.
        WaveIRuntimeKit.Target(runtime, walker.Value).SetWorldPosition(WaveIRuntimeKit.FarStart);
        WaveIRuntimeKit.Tick(runtime, ref tick, 30);
        if (dead)
        {
            WaveIRuntimeKit.Kill(runtime, walker.Value);
        }

        Vector3 start = WaveIRuntimeKit.Target(runtime, walker.Value).Position;
        for (int i = 0; i < 60; i++)
        {
            PlayerCommandFrame frame = Walk(walker, (ulong)(i + 1));
            if (secondary)
            {
                runtime.AdvanceSecondaryRemoteSession(frame, SimulationClock.FixedDeltaSeconds, PlayerSimulationTuning.AuthoritativeDefault);
                runtime.AdvanceAuthoritativeTick(++tick, SimulationClock.FixedDeltaSeconds, (PlayerCommandFrame?)null, PlayerSimulationTuning.AuthoritativeDefault);
            }
            else
            {
                runtime.AdvanceAuthoritativeTick(++tick, SimulationClock.FixedDeltaSeconds, frame, PlayerSimulationTuning.AuthoritativeDefault);
            }

            if (dead)
            {
                // Stay dead: the scan would otherwise respawn the player after 320 ticks.
                Assert.False(WaveIRuntimeKit.Target(runtime, walker.Value).IsAlive);
            }
        }

        return WaveIRuntimeKit.Target(runtime, walker.Value).Position - start;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeadSeat_NeitherWalksNorJumps_WhereALivingOneDoes(bool secondary)
    {
        Vector3 living = WalkFor(dead: false, secondary);
        Vector3 dead = WalkFor(dead: true, secondary);

        Assert.True(living.Length > 2f, $"the control moved only {living}: the input in this test is not valid");
        Assert.True(dead.Length < 0.3f, $"a dead player moved {dead}");
    }

    private static PlayerActionMessage Action(PlayerSessionId session, ulong sequence, IPlayerActionPayload payload) => new(session, sequence, payload);

    [Fact]
    public void ADeadPlayersBuildAndCombatActions_AreRejected_WithNoStateOrWriteChange_AndALivingOnesAreNot()
    {
        using MigrationTestDatabase db = new();
        using WaveIRuntimeKit.RealRun run = new(db);
        run.Host.JoinSession("deadman", "Deadman");
        PlayerSessionId session = new("deadman");
        WorldSessionRuntime runtime = run.Runtime;
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, "deadman");
        target.SetWorldPosition(new Vector3(30f, 1.5f, 30f));
        run.Advance(3);
        Vector3 place = target.EyePosition + new Vector3(1f, 0f, 0f);
        place = new Vector3(MathF.Floor(place.X), 2f, MathF.Floor(place.Z));

        // Control: alive, the same placement works.
        ulong sequence = 0;
        AuthoritativeActionResult built = runtime.ProcessAuthoritativeAction(Action(session, ++sequence, new BuildUseIntent(place)));
        Assert.True(built.Accepted, built.Rejection?.ToString());

        WaveIRuntimeKit.Kill(runtime, "deadman");
        run.Advance(1);
        Assert.False(target.IsAlive);
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);

        Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
        long writesBefore = runtime.PersistenceWrites.CompletedWrites;
        ulong mutationBefore = runtime.LastWorldMutationSequence;
        ulong checksumBefore = WorldStateChecksum.Compute(runtime.Map);
        EnemyActor wolf = WaveIRuntimeKit.AimWolfAt(runtime, "deadman", wolfHealth: 40);
        int wolfHealth = wolf.Combatant.Health.CurrentHealth;

        AuthoritativeActionResult build = runtime.ProcessAuthoritativeAction(
            Action(session, ++sequence, new BuildUseIntent(place + new Vector3(0f, 0f, 1f))));
        AuthoritativeActionResult attack = runtime.ProcessAuthoritativeAction(
            Action(session, ++sequence, new CombatUseIntent(target.EyePosition, target.Facing)));

        foreach (AuthoritativeActionResult result in new[] { build, attack })
        {
            Assert.False(result.Accepted);
            Assert.Contains("dead", result.Rejection!.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Null(result.Payload);
        }

        Assert.Equal(wolfHealth, wolf.Combatant.Health.CurrentHealth);
        Assert.Equal(mutationBefore, runtime.LastWorldMutationSequence);
        Assert.Equal(checksumBefore, WorldStateChecksum.Compute(runtime.Map));
        Assert.True(runtime.PersistenceWrites.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(writesBefore, runtime.PersistenceWrites.CompletedWrites);
        Assert.Equal(0, runtime.PersistenceWrites.FailedWrites);
    }

    [Fact]
    public void KilledByTheWolf_ThenItsOwnUseInTheSameTickIsRejected()
    {
        using WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.AttachRemoteSession(A, A.Value);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        target.Combatant.Health.SetAuthoritativeState(1, 100);
        ulong tick = 0;
        // The wolf's path to the start is blocked by terrain, so it starts beside the player.
        runtime.Enemies.ActiveActors.First().SetPosition(target.Position + new Vector3(0.8f, -0.5f, 0f));
        for (int i = 0; i < 600 && target.IsAlive; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
        }

        Assert.False(target.IsAlive, "the wolf never killed the player");

        // The same tick: the death was seen, and now the player's own queued use is processed.
        IReadOnlyList<AuthoritativeActionResult> results = runtime.ProcessQueuedAuthoritativeActions(
            tick,
            [new QueuedPlayerAction(1, A, Action(A, 1, new CombatUseIntent(target.EyePosition, target.Facing)))]);

        AuthoritativeActionResult result = Assert.Single(results);
        Assert.False(result.Accepted);
        Assert.Contains("dead", result.Rejection!.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoEnemyTargetsADeadPlayer()
    {
        using WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.AttachRemoteSession(A, A.Value);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        ulong tick = 0;
        EnemyActor wolf = runtime.Enemies.ActiveActors.First();
        wolf.SetPosition(target.Position + new Vector3(0.8f, -0.5f, 0f));

        // Well short of the respawn, so the player stays dead throughout.
        WaveIRuntimeKit.Tick(runtime, ref tick, 200);

        Assert.False(target.IsAlive);
        Assert.NotEqual(EnemyBehaviorState.Attacking, wolf.BehaviorState);
    }

    // ---- what a dead player may still do ----

    private static (WorldSessionRuntime Runtime, IAuthoritativePlayerTarget Target, string Key) DeadWithVest(out int vestSlot)
    {
        WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        ConnectedPlayer player = runtime.AttachRemoteSession(A, A.Value);
        string key = player.CharacterKey.Value;
        Assert.True(runtime.InventoryService.Ensure(key));
        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            runtime.InventoryService.TryGrantRolled(key, "gear.leather-vest", ItemRarity.Rare, new ItemRollSeed(1, 2, 3, 4, 0)));
        vestSlot = runtime.InventoryService.BuildSnapshot(key).Slots.First(slot => slot.Item?.ItemKind == "gear.leather-vest").SlotIndex;
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        return (runtime, target, key);
    }

    [Fact]
    public void ADeadPlayerCanEquip_ButStaysDead_AndRespawnUsesTheNewMaximum()
    {
        (WorldSessionRuntime runtime, IAuthoritativePlayerTarget target, _) = DeadWithVest(out int slot);
        using (runtime)
        {
            int oldMax = target.Combatant.Health.MaxHealth;
            ulong tick = 0;
            WaveIRuntimeKit.Tick(runtime, ref tick);

            AuthoritativeActionResult equip = runtime.ProcessAuthoritativeAction(Action(A, 1, new InventoryEquipIntent(slot)));

            Assert.True(equip.Accepted, equip.Rejection?.ToString());
            Assert.False(target.IsAlive);
            Assert.Equal(0, target.Combatant.Health.CurrentHealth);
            int newMax = target.Combatant.Health.MaxHealth;
            Assert.True(newMax > oldMax, $"the vest did not raise the maximum ({oldMax} -> {newMax})");

            WaveIRuntimeKit.Tick(runtime, ref tick, PlayerRespawnRules.DelayTicks);
            Assert.True(target.IsAlive);
            Assert.Equal(newMax, target.Combatant.Health.CurrentHealth);
        }
    }

    [Fact]
    public void ADeadPlayersStatsUpdate_IsAllowed_AndCannotHeal()
    {
        (WorldSessionRuntime runtime, IAuthoritativePlayerTarget target, _) = DeadWithVest(out _);
        using (runtime)
        {
            PlayerCombatStatsSnapshot stats = PlayerCombatStatsSnapshot.From(target.Combatant.BaseStats);
            AuthoritativeActionResult update = runtime.ProcessAuthoritativeAction(
                Action(A, 1, new PlayerStatsUpdateIntent(stats with { Vitality = stats.Vitality + 3 })));

            Assert.True(update.Accepted, update.Rejection?.ToString());
            Assert.False(target.IsAlive);
            Assert.Equal(0, target.Combatant.Health.CurrentHealth);
        }
    }
}
