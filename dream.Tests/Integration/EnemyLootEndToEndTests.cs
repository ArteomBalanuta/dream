using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// Wave I glue: all three streams together over real TCP on a real database, with a real PlayScene (no
// GL) as the client. A Dire Wolf killed at night pays a rolled item through the push route, the item is
// equipped and reduces the next bite, the player dies, waits, respawns at the spawn with the item, and
// the item survives a restart and a --worlds copy. The other tests here pin how a push interleaves with
// the messages around it: an inventory result, a world resync and a world transfer (plan risk 10).
[Collection(TimingSensitiveCollection.Name)]
public sealed class EnemyLootEndToEndTests(ITestOutputHelper output)
{
    private const string Session = "dire-e2e";
    private const string DireKind = "DireWolf";
    private const int DireDamage = 18;

    private static readonly Vector3 Spawn = new(5f, 1.5f, 5f);
    // Far from the spawn and the player, past every aggro range.
    private static readonly Vector3 Pen = new(45f, 1f, 45f);

    // The shipped content with one sure table: every kill pays one rare leather vest, the one kind whose
    // roll always carries armor. The shipped dire table drops nothing half the time.
    private static ContentCatalog SureVest()
    {
        string dir = ContentCatalog.ShippedDirectory;
        const string Table = "\",\"drops\":1,\"nothingWeight\":0,\"items\":[{\"item\":\"gear.leather-vest\",\"weight\":1}],\"rarity\":{\"common\":0,\"uncommon\":0,\"rare\":1}}";
        return ContentCatalog.Load(
            File.ReadAllText(Path.Combine(dir, ContentCatalog.ItemsFileName)),
            File.ReadAllText(Path.Combine(dir, ContentCatalog.EnemiesFileName)),
            "{\"version\":1,\"tables\":[{\"key\":\"wolf" + Table + ",{\"key\":\"dire-wolf" + Table + "]}");
    }

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db, string? world = null, ServerConnectionOptions? options = null)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(world)));
        WireLoopbackServer server = await WireLoopbackServer.StartAsync(host, options);
        server.Runtime.LootContent = SureVest();
        return server;
    }

    private static void MakeNight(WireLoopbackServer server) =>
        server.Runtime.ConfigureWorldClock(
            WorldClockSettings.Default,
            WaveT2NightSpawnTests.WorldTicksAt(WorldClockSettings.Default, dayIndex: 0, hour: 23));

    private static IAuthoritativePlayerTarget Target(WireLoopbackServer server) =>
        WaveIRuntimeKit.Target(server.Runtime, Session);

    private static EnemyActor? FindDire(WireLoopbackServer server) =>
        server.Runtime.Enemies.ActiveActors.FirstOrDefault(enemy => enemy.EnemyKind == DireKind);

    // Keeps every enemy but the one given out of the way, so only it can touch the player.
    private static void PenEnemies(WireLoopbackServer server, EnemyActor? except = null)
    {
        foreach (EnemyActor enemy in server.Runtime.Enemies.ActiveActors)
        {
            if (!ReferenceEquals(enemy, except))
            {
                enemy.SetPosition(Pen);
            }
        }
    }

    // Puts the enemy at 1 HP where the player's next attack lands.
    private static void AimAt(WireLoopbackServer server, EnemyActor enemy)
    {
        IAuthoritativePlayerTarget target = Target(server);
        enemy.SetPosition(target.EyePosition + (target.Facing * 2f) - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f));
        enemy.Combatant.Health.SetAuthoritativeState(1, enemy.Combatant.Health.MaxHealth);
    }

    // One command, one tick, and the scene has applied that tick's snapshot.
    private static async Task StepAsync(
        WireLoopbackServer server,
        HeadlessPlayScene client,
        bool attack = false,
        PlayerMoveIntent? move = null,
        EnemyActor? except = null)
    {
        PenEnemies(server, except);
        await client.SendCommandAsync(move ?? PlayerMoveIntent.None, Vector3.UnitZ, attack);
        await server.TickAsync();
        Assert.True(await client.FlushAsync());
        await client.PumpToSnapshotAsync(server.Runtime.CurrentServerTick + 1 - (ulong)server.Service.SnapshotIntervalTicks);
    }

    private static async Task<EnemyActor> WaitForDireAsync(WireLoopbackServer server, HeadlessPlayScene client)
    {
        for (int i = 0; i < 64; i++)
        {
            if (FindDire(server) is { } dire)
            {
                return dire;
            }

            await StepAsync(server, client);
        }

        throw new Xunit.Sdk.XunitException("no Dire Wolf spawned at night");
    }

    // Holds the attack on a 1 HP Dire in front of the player until the scene has applied a new push.
    private static async Task KillDireForLootAsync(WireLoopbackServer server, HeadlessPlayScene client)
    {
        int pushesBefore = client.Applied.OfType<InventoryPushMessage>().Count();
        for (int i = 0; i < 128; i++)
        {
            if (client.Applied.OfType<InventoryPushMessage>().Count() > pushesBefore)
            {
                return;
            }

            EnemyActor? dire = FindDire(server);
            if (dire != null)
            {
                AimAt(server, dire);
            }

            await StepAsync(server, client, attack: true, except: dire);
        }

        throw new Xunit.Sdk.XunitException("the kill never produced a push");
    }

    private static InventorySlotSnapshot RolledSlot(PlayerInventorySnapshot inventory) =>
        Assert.Single(inventory.Slots, slot => slot.Item is { IsRolled: true });

    private static string Text(InventoryItemSnapshot item) =>
        $"{item.ItemInstanceId}:{item.ItemKind}:{item.Rarity}:" +
        string.Join(',', (item.Modifiers ?? []).Select(m => $"{m.Stat}/{m.Op}/{BitConverter.SingleToInt32Bits(m.Value)}"));

    private static string WornRolled(PlayerInventorySnapshot inventory) =>
        Text(Assert.Single(inventory.Equipment ?? [], row => row.Item is { IsRolled: true }).Item!.Value);

    private static async Task<HeadlessPlayScene> EnterAtNightAsync(WireLoopbackServer server)
    {
        // Before the first tick: the world clock is fixed from then on.
        MakeNight(server);
        HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session);
        await StepAsync(server, client);
        return client;
    }

    private static async Task RestartAndCheckAsync(MigrationTestDatabase db, string? world, string worn)
    {
        await using WireLoopbackServer server = await StartAsync(db, world);
        await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session);
        Assert.Equal(worn, WornRolled(client.Inventory!.Value));
        Assert.True(client.World.Player.Combatant.EffectiveArmor > 0);
        Assert.Equal(server.Runtime.Player.Combatant.EffectiveArmor, client.World.Player.Combatant.EffectiveArmor);
        Assert.False(client.Death.IsDead);
        await client.LeaveAsync();
    }

    [Fact]
    public async Task ADireWolfsLoot_IsEquipped_ReducesTheNextBite_SurvivesDeathRespawnRestartAndACopy()
    {
        using MigrationTestDatabase db = new();
        string worn;
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            await using HeadlessPlayScene client = await EnterAtNightAsync(server);
            IAuthoritativePlayerTarget target = Target(server);
            Assert.Equal(0, target.Combatant.EffectiveArmor);
            EnemyActor dire = await WaitForDireAsync(server, client);
            Assert.Equal(3, dire.Level);

            // The kill: a rolled item through the scene's push branch, and no action result carries it.
            await KillDireForLootAsync(server, client);
            Assert.Equal("Looted: Leather Vest (Rare)", client.Loot.GetLine(0));
            InventorySlotSnapshot looted = RolledSlot(client.Inventory!.Value);
            Assert.Equal("gear.leather-vest", looted.Item!.Value.ItemKind);
            Assert.Equal(ItemRarity.Rare, looted.Item.Value.Rarity);
            Assert.All(client.Applied.OfType<ServerActionMessage>(), message => Assert.IsNotType<InventoryChangedResult>(message.Result.Payload));

            // Equip it with an intent; the client's armor comes from the scene's own read of the result.
            ulong sequence = await client.SendActionAsync(new InventoryEquipIntent(looted.SlotIndex));
            await StepAsync(server, client);
            await client.PumpUntilAsync(
                c => c.Applied.OfType<ServerActionMessage>().Any(m => m.Result.ActionSequence == sequence),
                "the equip result");
            int armor = target.Combatant.EffectiveArmor;
            Assert.True(armor > 0);
            Assert.Equal(armor, client.World.Player.Combatant.EffectiveArmor);

            // The next Dire's bite is mitigated, on the server and in what the client sees.
            int expectedBite = target.Combatant.MitigateDamage(DireDamage);
            Assert.InRange(expectedBite, 1, DireDamage - 1);
            int fullHealth = target.Combatant.Health.CurrentHealth;
            dire = await WaitForDireAsync(server, client);
            int bitten = 0;
            for (int i = 0; i < 256 && bitten == 0; i++)
            {
                dire.SetPosition(new Vector3(target.Position.X + 1f, 1f, target.Position.Z));
                await StepAsync(server, client, except: dire);
                bitten = fullHealth - target.Combatant.Health.CurrentHealth;
            }

            Assert.Equal(expectedBite, bitten);
            Assert.Equal(target.Combatant.Health.CurrentHealth, client.World.Player.Combatant.Health.CurrentHealth);
            output.WriteLine($"armor {armor}: a bite of {DireDamage} took {bitten}");

            // Dies to the Dire.
            target.Combatant.Health.SetAuthoritativeState(1, target.Combatant.Health.MaxHealth);
            for (int i = 0; i < 256 && target.IsAlive; i++)
            {
                dire.SetPosition(new Vector3(target.Position.X + 1f, 1f, target.Position.Z));
                await StepAsync(server, client, except: dire);
            }

            Assert.False(target.IsAlive);
            ulong deathTick = server.Runtime.CurrentServerTick;
            Assert.True(client.Death.IsDead);
            Assert.Equal(PlayerRespawnRules.DelayTicks / SimulationClock.AuthoritativeTicksPerSecond, client.Death.SecondsRemaining(deathTick));
            Vector3 fell = target.Position;

            // Waits, holding forward and attack: nothing moves, nothing attacks, the overlay counts down.
            int pushesBefore = client.Applied.OfType<InventoryPushMessage>().Count();
            while (server.Runtime.CurrentServerTick < deathTick + PlayerRespawnRules.DelayTicks - 1)
            {
                await StepAsync(server, client, attack: true, move: new PlayerMoveIntent(1f, 0f, JumpPressed: true));
                Assert.False(target.IsAlive);
                Assert.True(client.Death.IsDead);
            }

            Assert.True(Vector3.Distance(target.Position, fell) < 0.3f, $"moved to {target.Position} while dead");
            Assert.Equal(pushesBefore, client.Applied.OfType<InventoryPushMessage>().Count());
            Assert.Equal(1, client.Death.SecondsRemaining(server.Runtime.CurrentServerTick));

            // Respawns on its exact tick, at the spawn, full, with the item, and the overlay closes.
            await StepAsync(server, client);
            Assert.Equal(deathTick + PlayerRespawnRules.DelayTicks, server.Runtime.CurrentServerTick);
            Assert.True(target.IsAlive);
            Assert.True(Vector3.Distance(target.Position, Spawn) < 1f, $"respawned at {target.Position}");
            Assert.Equal(target.Combatant.Health.MaxHealth, target.Combatant.Health.CurrentHealth);
            Assert.False(client.Death.IsDead);
            Assert.True(client.World.Player.Combatant.Health.IsAlive);
            Assert.Equal(target.Combatant.Health.MaxHealth, client.World.Player.Combatant.Health.CurrentHealth);
            worn = WornRolled(client.Inventory!.Value);
            Assert.Equal(armor, client.World.Player.Combatant.EffectiveArmor);

            await client.LeaveAsync();
        }

        SqliteConnection.ClearAllPools();
        await RestartAndCheckAsync(db, world: null, worn);

        StringWriter stdout = new();
        StringWriter stderr = new();
        int exit = WorldCli.Run(["copy", "1", "dire-copy"], () => new SqliteWorldCatalog(db.Schema), stdout, stderr, id => WorldHostLease.TryAcquire(db.DatabasePath, id));
        Assert.True(exit == 0, stderr.ToString());
        SqliteConnection.ClearAllPools();
        await RestartAndCheckAsync(db, "dire-copy", worn);
    }

    // A kill's push must not be undone by an older inventory result. At the production snapshot interval
    // an action's result waits for the next snapshot and a push does not, so an equip one tick before the
    // kill used to arrive after the push, and the scene keeps the inventory it applies last.
    [Fact]
    public async Task AnInventoryResultStillWaitingForItsSnapshot_DoesNotUndoTheLootOfTheNextTick()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db, options: ServerConnectionOptions.Default);
        Assert.Equal(2, server.Service.SnapshotIntervalTicks);
        await using HeadlessPlayScene client = await EnterAtNightAsync(server);
        await WaitForDireAsync(server, client);
        await KillDireForLootAsync(server, client);
        InventorySlotSnapshot looted = RolledSlot(client.Inventory!.Value);

        // Let the attack come off cooldown, and stop on a tick that sent a snapshot: the next one will not.
        EnemyActor dire = await WaitForDireAsync(server, client);
        for (int i = 0; i < 2 * SimulationClock.AuthoritativeTicksPerSecond; i++)
        {
            await StepAsync(server, client);
        }

        long broadcasts = server.Service.SnapshotsBroadcast;
        while (server.Service.SnapshotsBroadcast == broadcasts)
        {
            await StepAsync(server, client);
        }

        // Equip on the tick with no snapshot: its result stays queued, with this tick's inventory.
        ulong sequence = await client.SendActionAsync(new InventoryEquipIntent(looted.SlotIndex));
        await StepAsync(server, client);
        Assert.DoesNotContain(client.Applied, message => message is ServerActionMessage m && m.Result.ActionSequence == sequence);

        // Kill on the next tick, which sends the snapshot and then that result.
        int pushes = client.Applied.OfType<InventoryPushMessage>().Count();
        AimAt(server, dire);
        await StepAsync(server, client, attack: true, except: dire);
        await client.PumpUntilAsync(
            c => c.Applied.OfType<ServerActionMessage>().Any(m => m.Result.ActionSequence == sequence),
            "the equip result");
        for (int i = 0; i < 8; i++)
        {
            await StepAsync(server, client);
        }

        Assert.True(client.Applied.OfType<InventoryPushMessage>().Count() > pushes, "no push followed the kill");
        PlayerInventorySnapshot authoritative = server.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(Session))!.Value;
        Assert.Equal(2, authoritative.Slots.Count(slot => slot.Item is { IsRolled: true }));
        Assert.Equal(2, client.Inventory!.Value.Slots.Count(slot => slot.Item is { IsRolled: true }));
    }

    // Closes wave H's "inventory actions while dead were not run over real TCP": equip works, the dead stay
    // at 0 (Vitality raises the maximum, not the health), and the respawn fills the new maximum.
    [Fact]
    public async Task AnEquipWhileDead_WorksOverRealTcp_StaysDead_AndTheRespawnUsesTheNewMaximum()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene client = await EnterAtNightAsync(server);
        await WaitForDireAsync(server, client);
        await KillDireForLootAsync(server, client);
        InventorySlotSnapshot looted = RolledSlot(client.Inventory!.Value);
        IAuthoritativePlayerTarget target = Target(server);
        int maximumBefore = target.Combatant.Health.MaxHealth;

        target.Combatant.Health.ApplyDamage(1_000_000);
        await StepAsync(server, client);
        Assert.True(client.Death.IsDead);

        ulong sequence = await client.SendActionAsync(new InventoryEquipIntent(looted.SlotIndex));
        await StepAsync(server, client);
        await client.PumpUntilAsync(
            c => c.Applied.OfType<ServerActionMessage>().Any(m => m.Result.ActionSequence == sequence),
            "the equip result");
        ServerActionMessage result = client.Applied.OfType<ServerActionMessage>().Single(m => m.Result.ActionSequence == sequence);
        Assert.True(result.Result.Accepted, result.Result.Rejection?.Reason);
        Assert.True(target.Combatant.EffectiveArmor > 0);
        Assert.Equal(0, target.Combatant.Health.CurrentHealth);
        Assert.False(target.IsAlive);
        Assert.True(target.Combatant.Health.MaxHealth > maximumBefore);

        for (int i = 0; i < PlayerRespawnRules.DelayTicks + 1 && !target.IsAlive; i++)
        {
            await StepAsync(server, client);
        }

        Assert.True(target.IsAlive);
        Assert.Equal(target.Combatant.Health.MaxHealth, target.Combatant.Health.CurrentHealth);
        Assert.Equal(target.Combatant.Health.MaxHealth, client.World.Player.Combatant.Health.CurrentHealth);
        Assert.False(client.Death.IsDead);
    }

    // Plan risk 10 and I2's note: a world resync is a snapshot, so it touches neither the inventory nor the
    // feed, in either order, and a push behind its carrier is held with the rest until the final chunk.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APushAndAWorldResyncOnOneConnection_LoseNothing_InEitherOrder(bool resyncFirst)
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene client = await EnterAtNightAsync(server);
        EnemyActor dire = await WaitForDireAsync(server, client);
        AimAt(server, dire);
        long transfers = client.Session.WorldTransfersCompleted;

        if (resyncFirst)
        {
            // Served at the end of the tick, ahead of that tick's push.
            Assert.True(server.Host.RequestWorldResyncIfConnected(client.Connection.ConnectionId));
        }

        // Nothing is applied until both are on the way, as when a frame hitches.
        PenEnemies(server, dire);
        await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: true);
        await server.TickAsync();

        if (!resyncFirst)
        {
            Assert.True(server.Host.RequestWorldResyncIfConnected(client.Connection.ConnectionId));
            await server.TickAsync();
        }

        Assert.True(await client.FlushAsync());
        await client.PumpUntilAsync(
            // The resync itself, not the session's transfer counter: the receive thread bumps the counter
            // just after it queues the world, so a pump that drained before the queue and checked after
            // the bump saw the counter ahead of Applied (resync index -1).
            c => c.Applied.OfType<InventoryPushMessage>().Any()
                && c.Applied.Any(message => message is ServerSnapshotMessage { Snapshot.World: not null }),
            "the push and the resync");
        Assert.True(client.Session.WorldTransfersCompleted > transfers);

        int push = client.Applied.ToList().FindIndex(message => message is InventoryPushMessage);
        int resync = client.Applied.ToList().FindIndex(message => message is ServerSnapshotMessage { Snapshot.World: not null });
        output.WriteLine($"resyncFirst={resyncFirst}: push at {push}, resync at {resync}");
        Assert.True(resync >= 0);
        Assert.Equal(resyncFirst, resync < push);

        Assert.Equal("Looted: Leather Vest (Rare)", client.Loot.GetLine(0));
        Assert.Equal("gear.leather-vest", RolledSlot(client.Inventory!.Value).Item!.Value.ItemKind);
    }

    // Plan risk 10: a kill while the client still receives the join's world. The push is behind the join
    // on one connection, so the join applies first and its reset of the feed cannot eat the notice.
    [Fact]
    public async Task APushDuringTheJoinsWorldTransfer_IsAppliedAfterTheJoin()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session, pumpJoin: false);
        Assert.Empty(client.Applied);

        // Straight after the join tick, with no frame run yet: the server already knows the character.
        await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
        await server.TickAsync();
        for (int i = 0; i < 64 && !client.Session.IsReceivingWorld && client.Applied.Count > 0; i++)
        {
            await Task.Delay(1);
        }

        for (int i = 0; i < 16; i++)
        {
            if (server.Runtime.Enemies.ActiveActors.Count > 0)
            {
                AimAt(server, server.Runtime.Enemies.ActiveActors[0]);
            }

            await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: true);
            await server.TickAsync();
        }

        Assert.True(await client.FlushAsync());
        await client.PumpUntilAsync(c => c.Applied.OfType<InventoryPushMessage>().Any(), "the push");

        int join = client.Applied.ToList().FindIndex(message => message is JoinAcceptedMessage);
        int push = client.Applied.ToList().FindIndex(message => message is InventoryPushMessage);
        Assert.True(join >= 0 && push > join, $"join at {join}, push at {push}");
        Assert.True(client.Session.WorldTransfersCompleted >= 1);
        Assert.StartsWith("Looted: Leather Vest (Rare)", client.Loot.GetLine(0), StringComparison.Ordinal);
        Assert.Contains(client.Inventory!.Value.Slots, slot => slot.Item is { IsRolled: true });
    }

    // The wire is hostile: through the scene's own branch a 1e99 modifier and 40 notices do not throw, show
    // at most the cap, and change no health or position.
    [Fact]
    public async Task AHostilePush_ThroughTheScene_ChangesNoHealthOrPosition()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session);
        PlayerCombatant player = client.World.Player.Combatant;
        Vector3 position = client.World.Player.GetWorldPosition();
        int health = player.Health.CurrentHealth;
        InventoryItemSnapshot item = new(
            1, "gear.leather-cap", "100% {0} Cap", "visual", 1, "d", [],
            [new StatModifier(StatId.Armor, StatOp.Flat, (float)1e99), new StatModifier(StatId.Vitality, StatOp.Flat, float.NaN)],
            HeldLightKind.None, EquipSlots: 1);
        InventoryPushMessage push = new(
            new PlayerInventorySnapshot("p", 4, 4, [], new InventorySlotSnapshot(100, item, IsEquipped: true)),
            [.. Enumerable.Range(0, 40).Select(i => new LootNotice($"{i}% {{0}}", (ItemRarity)(i % 7), i % 2 == 0))]);

        client.Scene.ApplyReplicatedMessage(push);

        Assert.Equal(position, client.World.Player.GetWorldPosition());
        Assert.Equal(health, player.Health.CurrentHealth);
        Assert.InRange(client.Loot.Count, 1, InventoryPushMessage.MaxNotices);
        await client.LeaveAsync();
    }
}
