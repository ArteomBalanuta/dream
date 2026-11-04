using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;
using Dream.Tests.Support;

namespace Dream.Tests.Integration;

// What the M4 workstream A loopback tests share: a real TCP server over a real SQLite database with the
// shipped enemies and vendor (EnemyContent = ContentCatalog.Shipped, then SpawnContentNpcs, as
// M3EndToEndLoopbackTests.StartAsync does; the test assembly's default content hides both), real
// HeadlessPlayScenes, and stepping that keeps the enemies out of the way.
internal static class M4AKit
{
    public static readonly Vector3 Pen = new(45f, 1f, 45f);
    public static readonly Vector3 NearTheVendor = new(5f, 1.5f, 5f);
    public static readonly Vector3 Away = new(20f, 1.5f, 20f);

    public static string KeyOf(string session) => WorldSessionRuntime.ResolveJoinPlayerName(session, $"Name {session}");

    public static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        server.Runtime.EnemyContent = ContentCatalog.Shipped;
        server.Runtime.SpawnContentNpcs();
        return server;
    }

    public static Task<HeadlessPlayScene> JoinAsync(WireLoopbackServer server, string sessionId) =>
        HeadlessPlayScene.JoinAsync(server, sessionId);

    public static IAuthoritativePlayerTarget Target(WireLoopbackServer server, string session) =>
        WaveIRuntimeKit.Target(server.Runtime, session);

    public static PlayerCombatant Combatant(WireLoopbackServer server, string session) =>
        Target(server, session).Combatant;

    // One command per scene, one tick, every scene has applied that tick's snapshot.
    public static async Task StepAsync(WireLoopbackServer server, params HeadlessPlayScene[] clients)
    {
        foreach (var enemy in server.Runtime.Enemies.ActiveActors)
        {
            enemy.SetPosition(Pen);
        }

        foreach (HeadlessPlayScene client in clients)
        {
            await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
        }

        await server.TickAsync();
        foreach (HeadlessPlayScene client in clients)
        {
            Assert.True(await client.FlushAsync());
        }

        ulong snapshot = server.Runtime.CurrentServerTick + 1 - (ulong)server.Service.SnapshotIntervalTicks;
        foreach (HeadlessPlayScene client in clients)
        {
            await client.PumpToSnapshotAsync(snapshot);
        }
    }

    public static async Task StepManyAsync(WireLoopbackServer server, int ticks, params HeadlessPlayScene[] clients)
    {
        for (int i = 0; i < ticks; i++)
        {
            await StepAsync(server, clients);
        }
    }

    public static async Task PlaceAsync(WireLoopbackServer server, HeadlessPlayScene who, Vector3 position, params HeadlessPlayScene[] all)
    {
        Target(server, who.SessionId).SetWorldPosition(position);
        who.World.Player.SetWorldPosition(position);
        await StepAsync(server, all);
        await StepAsync(server, all);
    }

    // Sends an action and ticks until its result is applied.
    public static async Task<ServerActionMessage> ActAsync(
        WireLoopbackServer server,
        HeadlessPlayScene who,
        IPlayerActionPayload payload,
        params HeadlessPlayScene[] all)
    {
        ulong sequence = await who.SendActionAsync(payload);
        for (int i = 0; i < 8; i++)
        {
            await StepAsync(server, all);
            who.Pump();
            if (who.Applied.OfType<ServerActionMessage>().FirstOrDefault(m => m.Result.ActionSequence == sequence && m.Result.SessionId == who.SessionId) is { } result)
            {
                return result;
            }
        }

        throw new Xunit.Sdk.XunitException($"no result for {who.SessionId}'s action {sequence}");
    }

    // Ticks until the next drain tick has passed, so a pending vitals message was sent.
    public static Task StepThroughVitalsDrainAsync(WireLoopbackServer server, params HeadlessPlayScene[] clients) =>
        StepManyAsync(server, WorldSessionRuntime.VitalsIntervalTicks + 1, clients);

    public static ServerPlayerVitals? LastVitals(HeadlessPlayScene client) =>
        client.Applied.OfType<ServerPlayerVitals>().LastOrDefault();

    // Buys one potion of the kind from the shipped vendor, with gold topped up for it. The client must
    // stand near the vendor; returns the player to where it was.
    public static async Task BuyAsync(WireLoopbackServer server, HeadlessPlayScene who, string kind, params HeadlessPlayScene[] all)
    {
        Vector3 back = who.World.Player.GetWorldPosition();
        await PlaceAsync(server, who, NearTheVendor, all);
        Assert.True(who.Npcs.TryGetNearest(who.World.Player.GetWorldPosition(), ClientNpcCache.InteractRange, out ClientNpc npc));
        Assert.True(server.Runtime.InventoryService.TryAddGold(KeyOf(who.SessionId), 100));
        ServerActionMessage bought = await ActAsync(server, who, new VendorBuyIntent(npc.ActorId, kind), all);
        Assert.True(bought.Result.Accepted, bought.Result.Rejection?.Reason);
        await PlaceAsync(server, who, back, all);
    }

    // The bag rows of the kind, in anchor order. The starter kit stocks a health and a mana potion
    // (StarterKitEntry v4, 80483b0), so a test that counts what it *bought* passes the ids it was
    // handed at join in except; with no exclusion set this is every row of the kind.
    public static InventorySlotSnapshot[] BagKind(HeadlessPlayScene client, string kind, HashSet<long>? except = null) =>
        [.. client.Inventory!.Value.BagItems()
            .Where(slot => slot.Item!.Value.ItemKind == kind && except?.Contains(slot.Item!.Value.ItemInstanceId) != true)
            .OrderBy(slot => slot.SlotIndex)];

    // Every item the character already owns, by instance id: the starter kit as it stands right now.
    // Captured after the join has been applied, before anything is bought.
    public static HashSet<long> StarterIds(HeadlessPlayScene client) =>
        [.. client.Inventory!.Value.AllItems().Select(item => item.ItemInstanceId)];

    public static long Count(MigrationTestDatabase db, string sql) => Convert.ToInt64(db.Scalar(sql));

    public static void Flush(WireLoopbackServer server)
    {
        Assert.True(server.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, server.Runtime.PersistenceWrites.FailedWrites);
    }
}

// Potions end to end (M4 workstream A): bought from the shipped vendor, used through ItemUseIntent over
// a real connection. The answer is an InventoryChangedResult (the host orders loot pushes against that
// payload); mana and cooldowns reach the owner alone as ServerPlayerVitals.
[Collection(TimingSensitiveCollection.Name)]
public sealed class ConsumableLoopbackTests
{
    private const string SessionA = "m4a-pot-a";
    private const string SessionB = "m4a-pot-b";
    private const string HealthPotion = "potion.health";
    private const string ManaPotion = "potion.mana";

    [Fact]
    public async Task APotion_BoughtFromTheShippedVendor_HealsOnce_IsConsumed_AndASecondUseInsideTheCooldownIsRefused()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);
        await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
        await using HeadlessPlayScene b = await M4AKit.JoinAsync(server, SessionB);
        await M4AKit.PlaceAsync(server, a, M4AKit.Away, a, b);
        await M4AKit.PlaceAsync(server, b, M4AKit.Away + new Vector3(3f, 0f, 0f), a, b);
        HashSet<long> starter = M4AKit.StarterIds(a);

        await M4AKit.BuyAsync(server, a, HealthPotion, a, b);
        await M4AKit.BuyAsync(server, a, HealthPotion, a, b);
        InventorySlotSnapshot[] potions = M4AKit.BagKind(a, HealthPotion, starter);
        Assert.Equal(2, potions.Length);

        PlayerCombatant combatant = M4AKit.Combatant(server, SessionA);
        int max = combatant.Health.MaxHealth;
        combatant.Health.SetAuthoritativeState(max - 80, max);

        // A wrong item id is refused before anything is spent.
        ServerActionMessage mismatch = await M4AKit.ActAsync(server, a, new ItemUseIntent(potions[0].SlotIndex, potions[0].Item!.Value.ItemInstanceId + 1000), a, b);
        Assert.False(mismatch.Result.Accepted);
        Assert.Equal(max - 80, combatant.Health.CurrentHealth);
        Assert.Equal(2, M4AKit.BagKind(a, HealthPotion, starter).Length);

        ServerActionMessage used = await M4AKit.ActAsync(server, a, new ItemUseIntent(potions[0].SlotIndex, potions[0].Item!.Value.ItemInstanceId), a, b);
        Assert.True(used.Result.Accepted, used.Result.Rejection?.Reason);
        Assert.IsType<InventoryChangedResult>(used.Result.Payload);
        Assert.Equal(max - 30, combatant.Health.CurrentHealth);
        Assert.Single(M4AKit.BagKind(a, HealthPotion, starter));
        Assert.DoesNotContain(a.Inventory!.Value.AllItems(), item => item.ItemInstanceId == potions[0].Item!.Value.ItemInstanceId);

        // The second potion, still hurt, but the kind is cooling down: refused, nothing spent.
        InventorySlotSnapshot second = M4AKit.BagKind(a, HealthPotion, starter)[0];
        ServerActionMessage refused = await M4AKit.ActAsync(server, a, new ItemUseIntent(second.SlotIndex, second.Item!.Value.ItemInstanceId), a, b);
        Assert.False(refused.Result.Accepted);
        Assert.Contains("cooldown", refused.Result.Rejection!.Reason, StringComparison.Ordinal);
        Assert.Equal(max - 30, combatant.Health.CurrentHealth);
        Assert.Single(M4AKit.BagKind(a, HealthPotion, starter));

        // The vitals say the kind is cooling down, to its owner only.
        await M4AKit.StepThroughVitalsDrainAsync(server, a, b);
        ServerPlayerVitals vitals = M4AKit.LastVitals(a)!;
        Assert.Contains(vitals.Cooldowns, c => c.Key == HealthPotion && c.ReadyTick > server.Runtime.CurrentServerTick);
        Assert.DoesNotContain(b.Applied.OfType<ServerPlayerVitals>().SelectMany(v => v.Cooldowns), c => c.Key == HealthPotion);

        // Once it ran out, the second potion works.
        await M4AKit.StepManyAsync(server, 330, a, b);
        ServerActionMessage again = await M4AKit.ActAsync(server, a, new ItemUseIntent(second.SlotIndex, second.Item!.Value.ItemInstanceId), a, b);
        Assert.True(again.Result.Accepted, again.Result.Rejection?.Reason);
        Assert.Equal(max, combatant.Health.CurrentHealth);
        Assert.Empty(M4AKit.BagKind(a, HealthPotion, starter));
    }

    [Fact]
    public async Task AfterAFailedWrite_APotionIsRefused_AndRestoresNothing_StartsNoCooldown_AndIsNotRemoved()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);
        await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
        await M4AKit.PlaceAsync(server, a, M4AKit.Away, a);
        HashSet<long> starter = M4AKit.StarterIds(a);
        await M4AKit.BuyAsync(server, a, HealthPotion, a);
        InventorySlotSnapshot potion = M4AKit.BagKind(a, HealthPotion, starter).Single();
        M4AKit.Flush(server);

        PlayerCombatant combatant = M4AKit.Combatant(server, SessionA);
        int max = combatant.Health.MaxHealth;
        combatant.Health.SetAuthoritativeState(max - 80, max);

        // Any wallet write now aborts: the next gold change is a failed write for this player.
        db.ExecuteScript("CREATE TRIGGER fail_wallet BEFORE UPDATE ON player_wallets BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        long failedBefore = server.Runtime.PersistenceWrites!.FailedWrites;
        Assert.True(server.Runtime.InventoryService.TryAddGold(M4AKit.KeyOf(SessionA), 5));
        Assert.True(server.Runtime.PersistenceWrites.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(failedBefore + 1, server.Runtime.PersistenceWrites.FailedWrites);

        ServerActionMessage used = await M4AKit.ActAsync(server, a, new ItemUseIntent(potion.SlotIndex, potion.Item!.Value.ItemInstanceId), a);

        Assert.False(used.Result.Accepted);
        Assert.Contains("save failed", used.Result.Rejection!.Reason, StringComparison.Ordinal);
        Assert.Equal(max - 80, combatant.Health.CurrentHealth);
        Assert.Single(M4AKit.BagKind(a, HealthPotion, starter));
        Assert.True(server.Runtime.Sessions.TryGet(new PlayerSessionId(SessionA), out ConnectedPlayer? player));
        Assert.True(player!.Cooldowns.IsReady(HealthPotion, server.Runtime.CurrentServerTick));
        db.ExecuteScript("DROP TRIGGER fail_wallet;");
    }

    [Fact]
    public async Task APotionAtFullVitals_IsRefused_AndSpendsNothing_AndAManaPotionRestoresManaAndReachesTheOwnerAsVitals()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);
        await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
        await using HeadlessPlayScene b = await M4AKit.JoinAsync(server, SessionB);
        await M4AKit.PlaceAsync(server, a, M4AKit.Away, a, b);
        await M4AKit.PlaceAsync(server, b, M4AKit.Away + new Vector3(3f, 0f, 0f), a, b);
        HashSet<long> starter = M4AKit.StarterIds(a);

        await M4AKit.BuyAsync(server, a, ManaPotion, a, b);
        InventorySlotSnapshot potion = M4AKit.BagKind(a, ManaPotion, starter).Single();
        PlayerCombatant combatant = M4AKit.Combatant(server, SessionA);
        Assert.Equal(combatant.MaxMana, combatant.CurrentMana);

        ServerActionMessage nothing = await M4AKit.ActAsync(server, a, new ItemUseIntent(potion.SlotIndex, potion.Item!.Value.ItemInstanceId), a, b);
        Assert.False(nothing.Result.Accepted);
        Assert.Contains("nothing to restore", nothing.Result.Rejection!.Reason, StringComparison.Ordinal);
        Assert.Single(M4AKit.BagKind(a, ManaPotion, starter));

        Assert.True(combatant.TrySpendMana(30));
        Assert.Equal(combatant.MaxMana - 30, combatant.CurrentMana);
        ServerActionMessage used = await M4AKit.ActAsync(server, a, new ItemUseIntent(potion.SlotIndex, potion.Item!.Value.ItemInstanceId), a, b);
        Assert.True(used.Result.Accepted, used.Result.Rejection?.Reason);
        Assert.Empty(M4AKit.BagKind(a, ManaPotion, starter));
        Assert.Equal(combatant.MaxMana, combatant.CurrentMana);

        // Owner only: A's vitals show A's mana, B's only ever show B's full mana.
        await M4AKit.StepThroughVitalsDrainAsync(server, a, b);
        ServerPlayerVitals vitalsA = M4AKit.LastVitals(a)!;
        Assert.Equal(combatant.MaxMana, vitalsA.MaxMana);
        Assert.Equal(combatant.CurrentMana, vitalsA.CurrentMana);
        Assert.All(b.Applied.OfType<ServerPlayerVitals>(), v => Assert.Equal(v.MaxMana, v.CurrentMana));
        Assert.Contains(vitalsA.Cooldowns, c => c.Key == ManaPotion);
    }

    [Fact]
    public async Task ManaRegenerates_ASpentSessionSendsVitalsWithoutAnyAction_AndASteadyFullSessionSendsNone()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);
        await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
        await using HeadlessPlayScene b = await M4AKit.JoinAsync(server, SessionB);
        await M4AKit.PlaceAsync(server, a, M4AKit.Away, a, b);
        await M4AKit.PlaceAsync(server, b, M4AKit.Away + new Vector3(3f, 0f, 0f), a, b);
        await M4AKit.StepThroughVitalsDrainAsync(server, a, b);
        int bBefore = b.Applied.OfType<ServerPlayerVitals>().Count();

        PlayerCombatant combatant = M4AKit.Combatant(server, SessionA);
        Assert.True(combatant.TrySpendMana(20));
        int low = combatant.CurrentMana;
        await M4AKit.StepManyAsync(server, PlayerManaRules.RegenIntervalTicks * 3, a, b);

        // 1 mana per interval at level 1: it rose, and A was told, B was not.
        Assert.True(combatant.CurrentMana > low, $"{low} -> {combatant.CurrentMana}");
        Assert.True(combatant.CurrentMana <= combatant.MaxMana);
        ServerPlayerVitals last = M4AKit.LastVitals(a)!;
        Assert.Equal(combatant.MaxMana, last.MaxMana);
        Assert.True(last.CurrentMana >= low);
        Assert.Equal(bBefore, b.Applied.OfType<ServerPlayerVitals>().Count());
    }
}
