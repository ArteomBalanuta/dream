using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Characters;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Simulation.Runtime;

// Wave I2 (I-D15..I-D20): a killed enemy rolls its loot table and the item goes into the killer's
// backpack, or is lost with a notice. The grant tests that matter run on the production repository
// (wave H1's mistake was testing only a stand-in): the owner column holds the character key, a rolled
// item survives a restart byte for byte, and a full backpack writes nothing.
public sealed class LootGrantTests
{
    private static readonly PlayerSessionId Killer = new("loot-killer");
    private static readonly PlayerSessionId Bystander = new("loot-bystander");

    private const string RenamedKey = "bob~2";

    private static string SlotText(InventorySlotSnapshot slot) =>
        $"{slot.SlotIndex}:{slot.Item?.ItemInstanceId}:{slot.Item?.ItemKind}:{slot.Item?.Rarity}:{slot.Item?.IsRolled}:" +
        string.Join(',', (slot.Item?.Modifiers ?? []).Select(m => $"{m.Stat}/{m.Op}/{BitConverter.SingleToInt32Bits(m.Value)}"));

    private static string[] Backpack(WorldSessionRuntime runtime, PlayerSessionId session) =>
        [.. runtime.TryCaptureInventorySnapshot(session)!.Value.Slots.Where(slot => slot.Item != null).Select(SlotText)];

    private static (string Owner, string Kind, string? Rolls)[] ItemRows(MigrationTestDatabase db)
    {
        SqliteConnection.ClearAllPools();
        using SqliteConnection connection = db.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT owner_player_name, item_kind, rolled_modifiers FROM item_instances WHERE world_id = 1 AND owner_player_name IS NOT NULL ORDER BY item_instance_id;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string, string, string?)> rows = [];
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return [.. rows];
    }

    // Seats the renamed character (key "bob~2", shown as "Bob") the way a join does, on the primary seat.
    private static void JoinRenamed(WaveIRuntimeKit.RealRun run)
    {
        run.Runtime.AttachRemoteSession(Killer, "Bob", new CharacterKey(RenamedKey));
        run.Runtime.RestorePersistedPlayerState(Killer, RenamedKey, "Bob", account: null);
    }

    [Fact]
    public void AKill_OnTheProductionRepository_GrantsARolledItemToTheCharacterKey_ThatSurvivesARestartByteForByte()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        string[] before;
        string[] afterKill;
        (string Owner, string Kind, string? Rolls)[] rowsAfterKill;
        using (WaveIRuntimeKit.RealRun run = new(db))
        {
            JoinRenamed(run);
            run.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1);
            WaveIRuntimeKit.Target(run.Runtime, Killer.Value).SetWorldPosition(WaveIRuntimeKit.FarStart);
            run.Advance(3);
            WaveIRuntimeKit.EnterCombat(run.Runtime, Killer);
            before = Backpack(run.Runtime, Killer);

            AuthoritativeActionResult hit = WaveIRuntimeKit.HitWolf(run.Runtime, Killer, actionSequence: 1);

            Assert.True(hit.Accepted, hit.Rejection?.Reason);
            Assert.True(Assert.IsType<CombatResolutionResult>(hit.Payload).TargetDied);
            afterKill = Backpack(run.Runtime, Killer);
            Assert.Equal(before.Length + 1, afterKill.Length);
            string gained = afterKill.Except(before).Single();
            Assert.Contains(":True:", gained, StringComparison.Ordinal);

            // The push carries the new snapshot and one gained notice, and nothing is left pending.
            Assert.True(run.Runtime.HasPendingInventoryPushes);
            List<SessionInventoryPush> pushes = [];
            run.Runtime.DrainInventoryPushesInto(pushes);
            SessionInventoryPush push = Assert.Single(pushes);
            Assert.Equal(Killer, push.SessionId);
            LootNotice notice = Assert.Single(push.Message.Notices);
            Assert.False(notice.Lost);
            Assert.NotNull(push.Message.Inventory);
            Assert.Equal(afterKill.Length, push.Message.Inventory!.Value.Slots.Count(slot => slot.Item != null));
            Assert.False(run.Runtime.HasPendingInventoryPushes);

            run.Runtime.DetachRemoteSession(Killer);
            Assert.True(run.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, run.Runtime.PersistenceWrites.FailedWrites);
            rowsAfterKill = ItemRows(db);
        }

        // The owner column holds the character key, never the display name.
        Assert.DoesNotContain(rowsAfterKill, row => row.Owner == "Bob");
        (string Owner, string Kind, string? Rolls)[] rolled = [.. rowsAfterKill.Where(row => row.Rolls != null)];
        (string owner, string kind, string? _) = Assert.Single(rolled);
        Assert.Equal(RenamedKey, owner);
        Assert.StartsWith("gear.", kind, StringComparison.Ordinal);

        using WaveIRuntimeKit.RealRun restarted = new(db);
        JoinRenamed(restarted);
        Assert.Equal(afterKill, Backpack(restarted.Runtime, Killer));
        restarted.Runtime.DetachRemoteSession(Killer);
        Assert.True(restarted.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(rowsAfterKill, ItemRows(db));
    }

    [Fact]
    public void AFullBackpack_AddsNoRow_SendsALostNoticeWithNoInventory_AndLosesNoExistingItem_OnTheProductionRepository()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        using WaveIRuntimeKit.RealRun run = new(db);
        JoinRenamed(run);
        run.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1);
        WaveIRuntimeKit.Target(run.Runtime, Killer.Value).SetWorldPosition(WaveIRuntimeKit.FarStart);
        run.Advance(3);
        WaveIRuntimeKit.EnterCombat(run.Runtime, Killer);

        int seed = 0;
        while (run.Runtime.InventoryService.TryGrantRolled(RenamedKey, "gear.leather-cap", ItemRarity.Common, new ItemRollSeed(7, 7, 7, 7, seed++)) == InventoryEquipOutcome.Equipped)
        {
            Assert.True(seed < 100, "the backpack never filled");
        }

        Assert.True(run.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
        string[] full = Backpack(run.Runtime, Killer);
        Assert.Equal(InventoryConstants.BackpackSlotCount, full.Length);
        int rowsBefore = ItemRows(db).Length;
        long itemsBefore = run.Runtime.NextItemInstanceId;

        AuthoritativeActionResult hit = WaveIRuntimeKit.HitWolf(run.Runtime, Killer, actionSequence: 1);

        Assert.True(Assert.IsType<CombatResolutionResult>(hit.Payload).TargetDied);
        Assert.Equal(full, Backpack(run.Runtime, Killer));
        Assert.Equal(itemsBefore, run.Runtime.NextItemInstanceId);
        List<SessionInventoryPush> pushes = [];
        run.Runtime.DrainInventoryPushesInto(pushes);
        InventoryPushMessage push = Assert.Single(pushes).Message;
        Assert.Null(push.Inventory);
        LootNotice notice = Assert.Single(push.Notices);
        Assert.True(notice.Lost);
        Assert.True(run.Runtime.PersistenceWrites.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(rowsBefore, ItemRows(db).Length);
        Assert.Equal(0, run.Runtime.PersistenceWrites.FailedWrites);
    }

    // ---- in-memory runtimes: who gets what, and what is bounded ----

    private static WorldSessionRuntime TwoHitters(out ulong tick)
    {
        WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1);
        foreach (PlayerSessionId session in new[] { Killer, Bystander })
        {
            ConnectedPlayer player = runtime.AttachRemoteSession(session, session.Value);
            Assert.True(runtime.InventoryService.Ensure(player.CharacterKey.Value));
        }

        tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 3);
        WaveIRuntimeKit.EnterCombat(runtime, Killer);
        WaveIRuntimeKit.EnterCombat(runtime, Bystander);
        return runtime;
    }

    [Fact]
    public void ASecondHitter_GetsNoItemAndNoExperience()
    {
        using WorldSessionRuntime runtime = TwoHitters(out _);
        int bystanderItems = Backpack(runtime, Bystander).Length;
        int bystanderExperience = WaveIRuntimeKit.Target(runtime, Bystander.Value).Combatant.Experience;
        int killerExperience = WaveIRuntimeKit.Target(runtime, Killer.Value).Combatant.Experience;

        AuthoritativeActionResult first = WaveIRuntimeKit.HitWolf(runtime, Killer, actionSequence: 1);
        // Same tick: the first fatal hit pruned the enemy, so the second finds nothing to hit.
        AuthoritativeActionResult second = runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
            Bystander,
            1,
            new CombatUseIntent(WaveIRuntimeKit.Target(runtime, Bystander.Value).EyePosition, WaveIRuntimeKit.Target(runtime, Bystander.Value).Facing)));

        Assert.True(Assert.IsType<CombatResolutionResult>(first.Payload).TargetDied);
        Assert.Null(Assert.IsType<CombatResolutionResult>(second.Payload).TargetActorId);
        Assert.Equal(bystanderItems, Backpack(runtime, Bystander).Length);
        Assert.Equal(bystanderExperience, WaveIRuntimeKit.Target(runtime, Bystander.Value).Combatant.Experience);
        Assert.True(WaveIRuntimeKit.Target(runtime, Killer.Value).Combatant.Experience > killerExperience);

        List<SessionInventoryPush> pushes = [];
        runtime.DrainInventoryPushesInto(pushes);
        Assert.Equal(Killer, Assert.Single(pushes).SessionId);
    }

    [Fact]
    public void TwoKillsInOneTick_GiveOnePushWithTwoNotices_AndTheSnapshotIsBuiltOnce()
    {
        using WorldSessionRuntime runtime = TwoHitters(out ulong tick);
        int before = Backpack(runtime, Killer).Length;

        runtime.GrantLoot(Killer, "Wolf", enemyActorId: 5000, tick);
        runtime.GrantLoot(Killer, "Wolf", enemyActorId: 5001, tick);

        List<SessionInventoryPush> pushes = [];
        runtime.DrainInventoryPushesInto(pushes);
        InventoryPushMessage push = Assert.Single(pushes).Message;
        Assert.Equal(2, push.Notices.Count);
        Assert.NotNull(push.Inventory);
        // Built once, when the push is drained (after both grants): that one snapshot already holds both items.
        Assert.Equal(before + 2, push.Inventory!.Value.Slots.Count(slot => slot.Item != null));
        Assert.False(runtime.HasPendingInventoryPushes);
    }

    [Fact]
    public void TwoKillers_OneHeld_DrainsExactlyTheOtherPush_AndKeepsTheHeldOneForTheNextDrain()
    {
        using WorldSessionRuntime runtime = TwoHitters(out ulong tick);
        runtime.GrantLoot(Killer, "Wolf", enemyActorId: 5000, tick);
        runtime.GrantLoot(Bystander, "Wolf", enemyActorId: 5001, tick);

        List<SessionInventoryPush> pushes = [];
        runtime.DrainInventoryPushesInto(pushes, [Bystander.Value]);
        Assert.Equal(Killer, Assert.Single(pushes).SessionId);
        Assert.True(runtime.HasPendingInventoryPushes);

        pushes.Clear();
        runtime.DrainInventoryPushesInto(pushes, []);
        SessionInventoryPush held = Assert.Single(pushes);
        Assert.Equal(Bystander, held.SessionId);
        Assert.Single(held.Message.Notices);
        Assert.NotNull(held.Message.Inventory);
        Assert.False(runtime.HasPendingInventoryPushes);
    }

    [Fact]
    public void PendingLoot_IsBoundedAtTheNoticeCap_WhateverTheHostDoes_AndDetachClearsIt()
    {
        using WorldSessionRuntime runtime = TwoHitters(out ulong tick);
        runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 4);

        // 40 kills that nobody drains: 160 rolls, the backpack fills after 16 and the rest are lost.
        for (int i = 0; i < 40; i++)
        {
            runtime.GrantLoot(Killer, "Wolf", enemyActorId: 6000 + i, tick);
        }

        List<SessionInventoryPush> pushes = [];
        runtime.DrainInventoryPushesInto(pushes);
        InventoryPushMessage push = Assert.Single(pushes).Message;
        Assert.Equal(InventoryPushMessage.MaxNotices, push.Notices.Count);

        runtime.GrantLoot(Killer, "Wolf", enemyActorId: 7000, tick);
        Assert.True(runtime.HasPendingInventoryPushes);
        runtime.DetachRemoteSession(Killer);
        Assert.False(runtime.HasPendingInventoryPushes);
    }

    private static InventoryPushMessage DrainOnly(WorldSessionRuntime runtime)
    {
        List<SessionInventoryPush> pushes = [];
        runtime.DrainInventoryPushesInto(pushes);
        return Assert.Single(pushes).Message;
    }

    [Fact]
    public void ALostNotice_PastTheCap_ReplacesTheLastOrdinaryOne_StaysInTheClientsTail_AndIsLogged()
    {
        using WorldSessionRuntime runtime = TwoHitters(out _);
        for (int i = 0; i < InventoryPushMessage.MaxNotices; i++)
        {
            runtime.AddLootNotice(Killer, new LootDrop($"ordinary-{i}", ItemRarity.Common), lost: false, inventoryChanged: true);
        }

        // The log is rate limited by a process-wide key; forget any earlier line of this key.
        NetworkDiagnosticsLog.ForgetRateLimitKey("server.loot.notice-dropped");
        List<(string Category, string Message)> lines = [];
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            runtime.AddLootNotice(Killer, new LootDrop("overflow", ItemRarity.Common), lost: true, inventoryChanged: false);
        }

        InventoryPushMessage push = DrainOnly(runtime);
        Assert.Equal(InventoryPushMessage.MaxNotices, push.Notices.Count);
        int lostIndex = Assert.Single(Enumerable.Range(0, push.Notices.Count), i => push.Notices[i].Lost);
        // LootFeedbackSystem keeps the last MaxLines lines of a push.
        Assert.True(lostIndex >= push.Notices.Count - Dream.Rendering.Hud.LootFeedbackSystem.MaxLines, $"lost notice at {lostIndex}");
        Assert.Equal(InventoryPushMessage.MaxNotices - 1, lostIndex);
        Assert.Equal("overflow", push.Notices[lostIndex].ItemDisplayName);
        Assert.Equal("ordinary-0", push.Notices[0].ItemDisplayName);
        Assert.NotNull(push.Inventory);

        (string category, string message) = Assert.Single(lines);
        Assert.Equal("server.loot.notice-dropped", category);
        Assert.Contains("item=overflow", message);
        Assert.Contains("lost=True", message);
        Assert.Contains($"replaced=ordinary-{InventoryPushMessage.MaxNotices - 1}", message);
    }

    [Fact]
    public void SeveralLostNotices_PastTheCap_FillTheTailFromTheEnd_AndAllLostStaysAtTheCap()
    {
        using WorldSessionRuntime runtime = TwoHitters(out _);
        for (int i = 0; i < InventoryPushMessage.MaxNotices; i++)
        {
            runtime.AddLootNotice(Killer, new LootDrop($"ordinary-{i}", ItemRarity.Common), lost: false, inventoryChanged: true);
        }

        for (int i = 0; i < 3; i++)
        {
            runtime.AddLootNotice(Killer, new LootDrop($"lost-{i}", ItemRarity.Common), lost: true, inventoryChanged: false);
        }

        InventoryPushMessage push = DrainOnly(runtime);
        int tail = push.Notices.Count - Dream.Rendering.Hud.LootFeedbackSystem.MaxLines;
        for (int i = 0; i < push.Notices.Count; i++)
        {
            Assert.Equal(i >= push.Notices.Count - 3, push.Notices[i].Lost);
            Assert.True(!push.Notices[i].Lost || i >= tail);
        }

        // Nothing but lost notices: the cap holds and nothing is replaced.
        for (int i = 0; i < 20; i++)
        {
            runtime.AddLootNotice(Killer, new LootDrop($"gone-{i}", ItemRarity.Common), lost: true, inventoryChanged: false);
        }

        InventoryPushMessage allLost = DrainOnly(runtime);
        Assert.Equal(InventoryPushMessage.MaxNotices, allLost.Notices.Count);
        Assert.All(allLost.Notices, notice => Assert.True(notice.Lost));
        Assert.Equal("gone-0", allLost.Notices[0].ItemDisplayName);
        Assert.Null(allLost.Inventory);
    }

    [Fact]
    public void AnInventoryThatIsNotLoaded_IsSkippedAndLogged_WithoutLoadingItOnTheTick()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 4);
        ConnectedPlayer player = runtime.AttachRemoteSession(Killer, Killer.Value);
        string key = player.CharacterKey.Value;
        Assert.False(runtime.InventoryService.IsLoaded(key));

        runtime.GrantLoot(Killer, "Wolf", enemyActorId: 5000, tick: 10);

        // Ensure would have loaded it synchronously, which can flush the write queue for seconds.
        Assert.False(runtime.InventoryService.IsLoaded(key));
        Assert.False(runtime.HasPendingInventoryPushes);
    }

    [Fact]
    public void AKillByASessionThatLeft_GrantsNothing()
    {
        using WorldSessionRuntime runtime = TwoHitters(out ulong tick);
        runtime.DetachRemoteSession(Killer);

        runtime.GrantLoot(Killer, "Wolf", enemyActorId: 5000, tick);
        runtime.GrantLoot(new PlayerSessionId("never-joined"), "Wolf", enemyActorId: 5001, tick);

        Assert.False(runtime.HasPendingInventoryPushes);
    }

    [Fact]
    public void AnEnemyKindWithNoTableOrNoTemplate_DropsNothing()
    {
        using WorldSessionRuntime runtime = TwoHitters(out ulong tick);

        runtime.GrantLoot(Killer, "NoSuchKind", enemyActorId: 5000, tick);

        Assert.False(runtime.HasPendingInventoryPushes);
    }

    [Fact]
    public void ADawnDespawn_DropsNothing()
    {
        using WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, WaveIRuntimeKit.FarStart, new SceneEventHub());
        runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 4);
        WorldClockSettings settings = WorldClockSettings.FromDayLengthMinutes(1);
        long nightTicks = (long)Math.Round(23 / 24d * settings.TicksPerDay) - settings.StartOffsetTicks;
        runtime.ConfigureWorldClock(settings, nightTicks);
        ConnectedPlayer player = runtime.AttachRemoteSession(Killer, Killer.Value);
        Assert.True(runtime.InventoryService.Ensure(player.CharacterKey.Value));
        int itemsBefore = Backpack(runtime, Killer).Length;

        ulong tick = 0;
        bool sawTwo = false;
        bool backToOne = false;
        for (int i = 0; i < 2400 && !backToOne; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            sawTwo |= runtime.Enemies.ActiveActors.Count(enemy => enemy.IsActive) >= 2;
            backToOne = sawTwo && runtime.WorldClock.Phase != WorldDayPhase.Night && runtime.Enemies.ActiveActors.Count(enemy => enemy.IsActive) == 1;
        }

        Assert.True(backToOne, "the night's second wolf never despawned at dawn");
        Assert.False(runtime.HasPendingInventoryPushes);
        Assert.Equal(itemsBefore, Backpack(runtime, Killer).Length);
    }
}
