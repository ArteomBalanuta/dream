using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server.Characters;
using Dream.Simulation.Characters;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Simulation.Runtime;

// Wave I tester: attacks the integrator did not write. Each test names the hole it probes. They sit on
// the same kit as the wave I2 tests (WaveIRuntimeKit), on real repositories where persistence is the point.
public sealed class WaveIAdversarialTests(ITestOutputHelper output)
{
    private const int Delay = PlayerRespawnRules.DelayTicks;

    private static readonly PlayerSessionId A = new("adv-a");
    private static readonly PlayerSessionId B = new("adv-b");

    private static PlayerActionMessage Action(PlayerSessionId session, ulong sequence, IPlayerActionPayload payload) => new(session, sequence, payload);

    private static string SlotText(InventorySlotSnapshot slot) =>
        $"{slot.SlotIndex}:{slot.Item?.ItemInstanceId}:{slot.Item?.ItemKind}:{slot.Item?.Rarity}:{slot.Item?.IsRolled}:" +
        string.Join(',', (slot.Item?.Modifiers ?? []).Select(m => $"{m.Stat}/{m.Op}/{BitConverter.SingleToInt32Bits(m.Value)}"));

    private static string[] Backpack(WorldSessionRuntime runtime, PlayerSessionId session) =>
        [.. runtime.TryCaptureInventorySnapshot(session)!.Value.Slots.Where(slot => slot.Item != null).Select(SlotText)];

    private static (string Owner, string Kind)[] ItemRows(MigrationTestDatabase db)
    {
        SqliteConnection.ClearAllPools();
        using SqliteConnection connection = db.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT owner_player_name, item_kind FROM item_instances WHERE world_id = 1 AND owner_player_name IS NOT NULL AND rolled_modifiers IS NOT NULL ORDER BY item_instance_id;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string, string)> rows = [];
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return [.. rows];
    }

    // Seats a character the way a join does (profile row first, then the restore that loads the inventory).
    private static ConnectedPlayer Join(WaveIRuntimeKit.RealRun run, PlayerSessionId session, string key, string display)
    {
        ConnectedPlayer player = run.Runtime.AttachRemoteSession(session, display, new CharacterKey(key));
        run.Runtime.RestorePersistedPlayerState(session, key, display, account: null);
        Assert.True(run.Runtime.InventoryService.IsLoaded(key));
        return player;
    }

    // Advances until the session is alive. Returns the ticks it took and leaves the run on that tick.
    private static int TicksUntilAlive(WaveIRuntimeKit.RealRun run, string session, int limit = Delay + 50)
    {
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(run.Runtime, session);
        int ticks = 0;
        while (!target.IsAlive)
        {
            Assert.True(ticks < limit, $"still dead after {ticks} ticks");
            run.Advance(1);
            ticks++;
        }

        return ticks;
    }

    // ---- kill and disconnect in the same tick ----

    [Fact]
    public void AKillAndALeaveInTheSameTick_KeepTheItem_QueueNoPush_AndLeaveNoPendingState()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        string[] kept;
        using (WaveIRuntimeKit.RealRun run = new(db))
        {
            Join(run, A, "adv-kill-leave", "Kl");
            run.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 2);
            WaveIRuntimeKit.Target(run.Runtime, A.Value).SetWorldPosition(WaveIRuntimeKit.FarStart);
            run.Advance(3);
            WaveIRuntimeKit.EnterCombat(run.Runtime, A);
            AuthoritativeActionResult kill = WaveIRuntimeKit.HitWolf(run.Runtime, A, actionSequence: 1);
            Assert.True(Assert.IsType<CombatResolutionResult>(kill.Payload).TargetDied);
            Assert.True(run.Runtime.HasPendingInventoryPushes);

            // The same tick, before the host drained: the connection drops.
            run.Runtime.DetachRemoteSession(A);

            Assert.False(run.Runtime.HasPendingInventoryPushes);
            List<SessionInventoryPush> pushes = [];
            run.Runtime.DrainInventoryPushesInto(pushes);
            Assert.Empty(pushes);

            // A late action from the departed session changes nothing and drops nothing.
            AuthoritativeActionResult late = run.Runtime.ProcessAuthoritativeAction(
                Action(A, 2, new CombatUseIntent(Vector3.Zero, Vector3.UnitZ)));
            Assert.False(late.Accepted);
            Assert.False(run.Runtime.HasPendingInventoryPushes);

            Assert.True(run.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, run.Runtime.PersistenceWrites.FailedWrites);
            Assert.Equal(2, ItemRows(db).Length);
            kept = [.. ItemRows(db).Select(row => row.Owner + ":" + row.Kind)];
            Assert.All(ItemRows(db), row => Assert.Equal("adv-kill-leave", row.Owner));
        }

        // The loot was granted before the leave, so a restart finds both items in the pack.
        using WaveIRuntimeKit.RealRun restarted = new(db);
        Join(restarted, A, "adv-kill-leave", "Kl");
        Assert.Equal(2, Backpack(restarted.Runtime, A).Count(text => text.Contains(":True:", StringComparison.Ordinal)));
        Assert.Equal(kept, ItemRows(db).Select(row => row.Owner + ":" + row.Kind));
    }

    [Fact]
    public void ADeathAndALeaveInTheSameTick_RejoinsDead_AndALeaveCannotShortenTheWait()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using WaveIRuntimeKit.RealRun run = new(db);
        Join(run, A, "adv-die-leave", "Dl");
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(run.Runtime, A.Value);
        target.SetWorldPosition(new Vector3(30f, 1.5f, 30f));
        run.Advance(5);

        WaveIRuntimeKit.Kill(run.Runtime, A.Value);
        run.Runtime.DetachRemoteSession(A);
        run.Advance(200);

        Join(run, A, "adv-die-leave", "Dl");
        target = WaveIRuntimeKit.Target(run.Runtime, A.Value);
        Assert.False(target.IsAlive);
        Assert.Equal(0, target.Combatant.Health.CurrentHealth);
        int waited = TicksUntilAlive(run, A.Value);

        // A fresh 320 after the rejoin (the first tick sees it dead and starts the wait), not the 120 left
        // of the original death.
        Assert.Equal(Delay + 1, waited);
        Assert.Equal(target.Combatant.Health.MaxHealth, target.Combatant.Health.CurrentHealth);
    }

    // The primary seat's Player is reused by successive sessions (I-D7). A stranger who takes it over from
    // a character that left dead must not inherit the corpse, and the dead one must still come back.
    [Fact]
    public void ANewCharacterTakingThePrimarySeatOfOneWhoLeftDead_StartsAliveAtFullHealth_AndTheDeadOneStillRespawnsOnReturn()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using WaveIRuntimeKit.RealRun run = new(db);
        run.Host.JoinSession("seat-first", "First");
        run.Advance(5);
        WaveIRuntimeKit.Kill(run.Runtime, "seat-first");
        run.Advance(10);
        run.Host.ReleaseSession("seat-first");

        run.Host.JoinSession("seat-second", "Second");
        IAuthoritativePlayerTarget second = WaveIRuntimeKit.Target(run.Runtime, "seat-second");
        Assert.True(second.IsAlive, "a new character inherited the previous occupant's death");
        Assert.Equal(second.Combatant.Health.MaxHealth, second.Combatant.Health.CurrentHealth);
        run.Advance(Delay + 20);
        Assert.True(second.IsAlive);
        Assert.Equal(second.Combatant.Health.MaxHealth, second.Combatant.Health.CurrentHealth);

        // The first character returns as a secondary seat (the primary is taken), still dead, and waits.
        run.Host.JoinSession("seat-first", "First");
        IAuthoritativePlayerTarget first = WaveIRuntimeKit.Target(run.Runtime, "seat-first");
        Assert.False(first.IsAlive);
        Assert.Equal(Delay + 1, TicksUntilAlive(run, "seat-first"));
        Assert.True(second.IsAlive);
    }

    // The mirror: the dead character takes the primary seat back from a stranger who has since left.
    [Fact]
    public void ADeadCharacterReturningToThePrimarySeatAfterAStranger_IsDeadUntilItsOwnWaitEnds()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using WaveIRuntimeKit.RealRun run = new(db);
        run.Host.JoinSession("seat-first", "First");
        run.Advance(5);
        WaveIRuntimeKit.Kill(run.Runtime, "seat-first");
        run.Advance(10);
        run.Host.ReleaseSession("seat-first");
        run.Host.JoinSession("seat-second", "Second");
        run.Advance(20);
        run.Host.ReleaseSession("seat-second");

        run.Host.JoinSession("seat-first", "First");
        IAuthoritativePlayerTarget first = WaveIRuntimeKit.Target(run.Runtime, "seat-first");
        Assert.False(first.IsAlive);
        Assert.Equal(0, first.Combatant.Health.CurrentHealth);
        Assert.Equal(Delay + 1, TicksUntilAlive(run, "seat-first"));
    }

    // ---- loot into a backpack that fills in the same tick ----

    [Fact]
    public void TwoKillsInOneTick_IntoAPackWithOneFreeSlot_GiveOneItemAndOneLostNotice_InOrder()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1);
        ConnectedPlayer player = runtime.AttachRemoteSession(A, A.Value);
        string key = player.CharacterKey.Value;
        Assert.True(runtime.InventoryService.Ensure(key));
        int existing = runtime.InventoryService.BuildSnapshot(key).Slots.Count(slot => slot.Item != null);
        for (int i = 0; i < InventoryConstants.BackpackSlotCount - existing - 1; i++)
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, runtime.InventoryService.TryGrantRolled(key, "gear.leather-cap", ItemRarity.Common, new ItemRollSeed(3, 3, 3, 3, i)));
        }

        string[] beforeKills = Backpack(runtime, A);
        Assert.Equal(InventoryConstants.BackpackSlotCount - 1, beforeKills.Length);

        runtime.GrantLoot(A, "Wolf", enemyActorId: 9000, tick: 50);
        runtime.GrantLoot(A, "Wolf", enemyActorId: 9001, tick: 50);

        List<SessionInventoryPush> pushes = [];
        runtime.DrainInventoryPushesInto(pushes);
        InventoryPushMessage push = Assert.Single(pushes).Message;
        Assert.Equal([false, true], push.Notices.Select(notice => notice.Lost).ToArray());
        Assert.NotNull(push.Inventory);
        Assert.Equal(InventoryConstants.BackpackSlotCount, push.Inventory!.Value.Slots.Count(slot => slot.Item != null));
        Assert.Equal(beforeKills, Backpack(runtime, A).Take(beforeKills.Length).ToArray());
    }

    [Fact]
    public void AFourDropTableIntoThreeFreeSlots_GainsThree_LosesOne_AndTheLossesOfOtherPlayersAreTheirOwn()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 4);
        foreach (PlayerSessionId session in new[] { A, B })
        {
            ConnectedPlayer player = runtime.AttachRemoteSession(session, session.Value);
            string key = player.CharacterKey.Value;
            Assert.True(runtime.InventoryService.Ensure(key));
            int existing = runtime.InventoryService.BuildSnapshot(key).Slots.Count(slot => slot.Item != null);
            int fill = InventoryConstants.BackpackSlotCount - existing - (session == A ? 3 : 4);
            for (int i = 0; i < fill; i++)
            {
                Assert.Equal(InventoryEquipOutcome.Equipped, runtime.InventoryService.TryGrantRolled(key, "gear.leather-cap", ItemRarity.Common, new ItemRollSeed(4, 4, 4, 4, i)));
            }
        }

        // One tick, two killers: A has 3 free slots for 4 drops, B has exactly 4.
        runtime.GrantLoot(A, "Wolf", enemyActorId: 9100, tick: 70);
        runtime.GrantLoot(B, "Wolf", enemyActorId: 9101, tick: 70);

        List<SessionInventoryPush> pushes = [];
        runtime.DrainInventoryPushesInto(pushes);
        Assert.Equal(2, pushes.Count);
        InventoryPushMessage a = pushes.Single(push => push.SessionId == A).Message;
        InventoryPushMessage b = pushes.Single(push => push.SessionId == B).Message;
        Assert.Equal(3, a.Notices.Count(notice => !notice.Lost));
        Assert.Equal(1, a.Notices.Count(notice => notice.Lost));
        Assert.Equal(4, b.Notices.Count(notice => !notice.Lost));
        Assert.Empty(b.Notices.Where(notice => notice.Lost));
        Assert.Equal(InventoryConstants.BackpackSlotCount, a.Inventory!.Value.Slots.Count(slot => slot.Item != null));
        Assert.Equal(InventoryConstants.BackpackSlotCount, b.Inventory!.Value.Slots.Count(slot => slot.Item != null));
    }

    // ---- equip and unequip around death and respawn ----

    private static int BackpackSlotOf(WorldSessionRuntime runtime, string key, string kind) =>
        runtime.InventoryService.BuildSnapshot(key).Slots.First(slot => slot.Item?.ItemKind == kind).SlotIndex;

    private static int WornSlotOf(WorldSessionRuntime runtime, string key, string kind) =>
        runtime.InventoryService.BuildSnapshot(key).Equipment!.First(slot => slot.Item?.ItemKind == kind).SlotIndex;

    [Fact]
    public void EquipAndUnequipAroundDeathAndRespawn_NeverHealTheDead_AndTheRespawnUsesTheMaximumOfTheMoment()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        ConnectedPlayer player = runtime.AttachRemoteSession(A, A.Value);
        string key = player.CharacterKey.Value;
        Assert.True(runtime.InventoryService.Ensure(key));
        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            runtime.InventoryService.TryGrantRolled(key, "gear.leather-vest", ItemRarity.Rare, new ItemRollSeed(1, 2, 3, 4, 0)));
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        int baseMax = target.Combatant.Health.MaxHealth;
        ulong tick = 0;
        ulong sequence = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick);

        void Equip()
        {
            AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(
                Action(A, ++sequence, new InventoryEquipIntent(BackpackSlotOf(runtime, key, "gear.leather-vest"))));
            Assert.True(result.Accepted, result.Rejection?.ToString());
        }

        void Unequip()
        {
            AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(
                Action(A, ++sequence, new InventoryUnequipIntent(WornSlotOf(runtime, key, "gear.leather-vest"))));
            Assert.True(result.Accepted, result.Rejection?.ToString());
        }

        WaveIRuntimeKit.Kill(runtime, A.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.False(target.IsAlive);

        // Dead: wear it, take it off, wear it again. The maximum follows; the health never leaves 0.
        Equip();
        int vestMax = target.Combatant.Health.MaxHealth;
        Assert.True(vestMax > baseMax, $"{baseMax} -> {vestMax}");
        Assert.Equal(0, target.Combatant.Health.CurrentHealth);
        Unequip();
        Assert.Equal(baseMax, target.Combatant.Health.MaxHealth);
        Assert.Equal(0, target.Combatant.Health.CurrentHealth);
        Equip();
        Assert.Equal(0, target.Combatant.Health.CurrentHealth);

        // The last tick of the wait, then the vest comes off before the respawn tick runs.
        WaveIRuntimeKit.Tick(runtime, ref tick, Delay - 1);
        Assert.False(target.IsAlive);
        Unequip();
        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.True(target.IsAlive);
        Assert.Equal(baseMax, target.Combatant.Health.MaxHealth);
        Assert.Equal(baseMax, target.Combatant.Health.CurrentHealth);
        Assert.Equal(18, target.Combatant.MitigateDamage(18));

        // Alive at full health, putting it on keeps the ratio, so the new maximum is full.
        Equip();
        Assert.Equal(vestMax, target.Combatant.Health.MaxHealth);
        Assert.Equal(vestMax, target.Combatant.Health.CurrentHealth);
        Assert.True(target.Combatant.MitigateDamage(18) < 18);

        // Worn through a second death: still worn after the respawn, with its armor and maximum.
        WaveIRuntimeKit.Kill(runtime, A.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick, Delay + 1);
        Assert.True(target.IsAlive);
        Assert.Equal(vestMax, target.Combatant.Health.CurrentHealth);
        Assert.True(target.Combatant.EffectiveArmor > 0);
        Assert.Equal("gear.leather-vest", runtime.InventoryService.BuildSnapshot(key).Equipment!.Single(slot => slot.Item != null).Item!.Value.ItemKind);
    }

    // ---- rename or release with a pending respawn or loot ----

    [Fact]
    public void ARenameBetweenDeathAndRespawn_KeepsTheLootUnderTheKey_AndTheDeadStayDead()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        const string Key = "adv-rename";
        string[] pack;
        using (WaveIRuntimeKit.RealRun run = new(db))
        {
            Join(run, A, Key, Key);
            run.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1);
            WaveIRuntimeKit.Target(run.Runtime, A.Value).SetWorldPosition(WaveIRuntimeKit.FarStart);
            run.Advance(3);
            WaveIRuntimeKit.EnterCombat(run.Runtime, A);
            Assert.True(Assert.IsType<CombatResolutionResult>(WaveIRuntimeKit.HitWolf(run.Runtime, A, 1).Payload).TargetDied);
            WaveIRuntimeKit.Kill(run.Runtime, A.Value);
            run.Advance(100);
            pack = Backpack(run.Runtime, A);
            run.Runtime.DetachRemoteSession(A);
        }

        // The server is stopped (rename is offline): the display name changes, the key never does.
        StringWriter output = new();
        StringWriter error = new();
        int exit = CharacterCli.Run(["rename", Key, "Renamed"], () => db.Schema, output, error);
        Assert.True(exit == 0, error.ToString());

        using WaveIRuntimeKit.RealRun restarted = new(db);
        Join(restarted, A, Key, "Renamed");
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(restarted.Runtime, A.Value);
        Assert.False(target.IsAlive);
        Assert.Equal(pack, Backpack(restarted.Runtime, A));
        Assert.Equal(Delay + 1, TicksUntilAlive(restarted, A.Value));
        Assert.All(ItemRows(db), row => Assert.Equal(Key, row.Owner));
        Assert.Equal("Renamed", db.Scalar($"SELECT display_name FROM player_profiles WHERE player_name = '{Key}';"));
    }

    [Fact]
    public void AReleaseWhileLootIsPending_StillDeliversThePush_AndChangesNoOwner()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        const string Key = "adv-release";
        db.ExecuteScript($"INSERT INTO player_profiles (player_name, display_name, level) VALUES ('{Key}', 'Rel', 1);");
        using WaveIRuntimeKit.RealRun run = new(db);
        Join(run, A, Key, "Rel");
        run.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1);
        WaveIRuntimeKit.Target(run.Runtime, A.Value).SetWorldPosition(WaveIRuntimeKit.FarStart);
        run.Advance(3);
        WaveIRuntimeKit.EnterCombat(run.Runtime, A);
        Assert.True(Assert.IsType<CombatResolutionResult>(WaveIRuntimeKit.HitWolf(run.Runtime, A, 1).Payload).TargetDied);
        Assert.True(run.Runtime.HasPendingInventoryPushes);

        // The operator releases the character while the server runs (release takes no lease).
        StringWriter output = new();
        StringWriter error = new();
        int exit = CharacterCli.Run(["release", Key], () => db.Schema, output, error);
        Assert.True(exit == 0, error.ToString());

        List<SessionInventoryPush> pushes = [];
        run.Runtime.DrainInventoryPushesInto(pushes);
        InventoryPushMessage push = Assert.Single(pushes).Message;
        Assert.NotNull(push.Inventory);
        Assert.False(Assert.Single(push.Notices).Lost);

        run.Runtime.DetachRemoteSession(A);
        Assert.True(run.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, run.Runtime.PersistenceWrites.FailedWrites);
        Assert.Equal(Key, Assert.Single(ItemRows(db)).Owner);
        Assert.Equal(1L, Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM player_profiles WHERE player_name = '{Key}';"), System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- restart between death and respawn, both kinds of seat ----

    [Fact]
    public void ARestartWithTwoDeadSeats_RespawnsEachOnItsOwnFreshWait_AtTheSpawn()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using (WaveIRuntimeKit.RealRun run = new(db))
        {
            Join(run, A, "adv-seat-a", "Sa");
            Join(run, B, "adv-seat-b", "Sb");
            WaveIRuntimeKit.Target(run.Runtime, A.Value).SetWorldPosition(new Vector3(10f, 1.5f, 10f));
            WaveIRuntimeKit.Target(run.Runtime, B.Value).SetWorldPosition(new Vector3(20f, 1.5f, 20f));
            run.Advance(10);
            WaveIRuntimeKit.Kill(run.Runtime, B.Value);
            run.Advance(30);
            WaveIRuntimeKit.Kill(run.Runtime, A.Value);
            run.Advance(100);
            run.Runtime.DetachRemoteSession(B);
            run.Runtime.DetachRemoteSession(A);
        }

        using WaveIRuntimeKit.RealRun restarted = new(db);
        Join(restarted, A, "adv-seat-a", "Sa");
        Join(restarted, B, "adv-seat-b", "Sb");
        IAuthoritativePlayerTarget a = WaveIRuntimeKit.Target(restarted.Runtime, A.Value);
        IAuthoritativePlayerTarget b = WaveIRuntimeKit.Target(restarted.Runtime, B.Value);
        Assert.False(a.IsAlive);
        Assert.False(b.IsAlive);
        Assert.True(Math.Abs(a.Position.X - 10f) < 1f && Math.Abs(b.Position.X - 20f) < 1f, "they restored where they fell");

        // The restart restarts both waits at the first tick; both seats are scanned in the same tick.
        int waited = 0;
        while (!(a.IsAlive && b.IsAlive))
        {
            Assert.True(waited < Delay + 50, "a seat never came back");
            restarted.Advance(1);
            waited++;
        }

        Assert.Equal(Delay + 1, waited);
        Assert.Equal(a.Combatant.Health.MaxHealth, a.Combatant.Health.CurrentHealth);
        Assert.Equal(b.Combatant.Health.MaxHealth, b.Combatant.Health.CurrentHealth);
        // The server's world spawn is (5, 1.5, 5).
        Assert.True(Vector2.Distance(new Vector2(a.Position.X, a.Position.Z), new Vector2(5f, 5f)) < 2f, a.Position.ToString());
        Assert.True(Vector2.Distance(new Vector2(b.Position.X, b.Position.Z), new Vector2(5f, 5f)) < 2f, b.Position.ToString());
        output.WriteLine($"a {a.Position} b {b.Position}");
    }

    // ---- item rolls with extreme stats in the armor formula ----

    [Theory]
    [InlineData(float.MaxValue)]
    [InlineData(1e30f)]
    [InlineData(1001f)]
    [InlineData(1000f)]
    [InlineData(1f)]
    [InlineData(-1f)]
    [InlineData(-1e30f)]
    [InlineData(-float.MaxValue)]
    public void ExtremeArmorModifiers_KeepTheFactorInRange_AndTheMitigatedHitBetween1AndTheRawAmount(float armor)
    {
        foreach (EquipmentSlot slot in Enum.GetValues<EquipmentSlot>())
        {
            PlayerCombatant combatant = new(() => new Vector3(1f, 1f, 0.5f));
            EquipmentLoadout loadout = new();
            // Two sources at the same extreme: their sum overflows float to infinity when armor is huge.
            loadout.Set(slot, [new StatModifier(StatId.Armor, StatOp.Flat, armor)]);
            loadout.Set(slot == EquipmentSlot.Body ? EquipmentSlot.Head : EquipmentSlot.Body, [new StatModifier(StatId.Armor, StatOp.Flat, armor)]);
            combatant.ApplyEquipment(loadout);

            int effective = combatant.EffectiveArmor;
            Assert.InRange(effective, 0, 1000);
            Assert.InRange(combatant.DamageTakenFactor, 100d / 1100d - 1e-12, 1d);
            foreach (int amount in new[] { 1, 2, 12, 18, 1000, 1_000_000, int.MaxValue })
            {
                int mitigated = combatant.MitigateDamage(amount);
                Assert.InRange(mitigated, 1, amount);
                if (effective == 0)
                {
                    Assert.Equal(amount, mitigated);
                }
            }

            combatant.Health.SetAuthoritativeState(500, 500);
            DamageReceipt receipt = combatant.ApplyDamage(int.MaxValue);
            Assert.True(receipt.WasFatal);
            Assert.Equal(0, combatant.Health.CurrentHealth);
        }
    }

    [Fact]
    public void AnInfiniteSumTimesAZeroPercent_IsNaN_AndMeansNoArmor_NotAThrow()
    {
        PlayerCombatant combatant = new(() => new Vector3(1f, 1f, 0.5f));
        EquipmentLoadout loadout = new();
        loadout.Set(EquipmentSlot.Body, [new StatModifier(StatId.Armor, StatOp.Flat, float.MaxValue)]);
        loadout.Set(EquipmentSlot.Head, [new StatModifier(StatId.Armor, StatOp.Flat, float.MaxValue)]);
        loadout.Set(EquipmentSlot.MainHand, [new StatModifier(StatId.Armor, StatOp.Percent, -1f)]);

        combatant.ApplyEquipment(loadout);

        Assert.Equal(0, combatant.EffectiveArmor);
        Assert.Equal(1.0, combatant.DamageTakenFactor);
        Assert.Equal(18, combatant.MitigateDamage(18));
    }

    [Fact]
    public void ArmorFlippingBetweenZeroAndTheCap_ReEvaluatesTheFactorEveryTime_AndMitigationNeverIncreasesWithArmor()
    {
        PlayerCombatant combatant = new(() => new Vector3(1f, 1f, 0.5f));
        int previous = int.MaxValue;
        for (int armor = 0; armor <= 1000; armor += 7)
        {
            EquipmentLoadout loadout = new();
            if (armor > 0)
            {
                loadout.Set(EquipmentSlot.Body, [new StatModifier(StatId.Armor, StatOp.Flat, armor)]);
            }

            combatant.ApplyEquipment(loadout);
            int bite = combatant.MitigateDamage(18);
            Assert.True(bite <= previous, $"armor {armor}: {bite} after {previous}");
            Assert.InRange(bite, 1, 18);
            previous = bite;

            // Back to no armor and forth: the cache must follow, not remember.
            combatant.ApplyEquipment(new EquipmentLoadout());
            Assert.Equal(18, combatant.MitigateDamage(18));
        }
    }
}
