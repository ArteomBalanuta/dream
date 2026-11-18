using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// M3 workstream B over real TCP on a real database, with a real PlayScene (no GL) as the client: a kill
// pays gold, the vendor replicates as an actor, and every trade is checked by the server again (range,
// stock, gold, the item named). The wallet is a delta inside each action's own write, so a write that fails
// leaves gold and item together, and the gold survives a restart.
public sealed class WalletVendorLoopbackTests
{
    private const string Session = "vendor-e2e";
    private const string Torch = "tool.torch";

    private static readonly Vector3 Pen = new(45f, 1f, 45f);
    private static readonly Vector3 Spawn = new(5f, 1.5f, 5f);
    private static readonly Vector3 FarFromTheVendor = new(30f, 1.5f, 30f);

    private static string CharacterKey => WorldSessionRuntime.ResolveJoinPlayerName(Session, $"Name {Session}");

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        // The test assembly's default content has no camps and no NPCs (LegacyWolfPopulation), so the
        // scenario keeps its one wolf. Add the shipped vendor to that, as the production host spawns it.
        string dir = ContentCatalog.ShippedDirectory;
        server.Runtime.EnemyContent = ContentCatalog.Load(
            File.ReadAllText(Path.Combine(dir, ContentCatalog.ItemsFileName)),
            File.ReadAllText(Path.Combine(dir, ContentCatalog.EnemiesFileName)),
            File.ReadAllText(Path.Combine(dir, ContentCatalog.LootFileName)),
            "{\"version\":1,\"camps\":[]}",
            File.ReadAllText(Path.Combine(dir, ContentCatalog.NpcsFileName)));
        server.Runtime.SpawnContentNpcs();
        return server;
    }

    private static IAuthoritativePlayerTarget Target(WireLoopbackServer server) =>
        WaveIRuntimeKit.Target(server.Runtime, Session);

    // One command, one tick, and the scene has applied that tick's snapshot. Enemies are kept out of the
    // way, except the one given.
    private static async Task StepAsync(WireLoopbackServer server, HeadlessPlayScene client, bool attack = false, EnemyActor? except = null)
    {
        foreach (EnemyActor enemy in server.Runtime.Enemies.ActiveActors)
        {
            if (!ReferenceEquals(enemy, except))
            {
                enemy.SetPosition(Pen);
            }
        }

        await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack);
        await server.TickAsync();
        Assert.True(await client.FlushAsync());
        await client.PumpToSnapshotAsync(server.Runtime.CurrentServerTick + 1 - (ulong)server.Service.SnapshotIntervalTicks);
    }

    // Sends the action and steps until its result has reached the scene.
    private static async Task<ServerActionMessage> ActAsync(WireLoopbackServer server, HeadlessPlayScene client, IPlayerActionPayload payload)
    {
        ulong sequence = await client.SendActionAsync(payload);
        for (int i = 0; i < 8; i++)
        {
            await StepAsync(server, client);
            client.Pump();
            if (client.Applied.OfType<ServerActionMessage>().FirstOrDefault(m => m.Result.ActionSequence == sequence) is { } result)
            {
                return result;
            }
        }

        throw new Xunit.Sdk.XunitException($"no result for action {sequence}");
    }

    private static void AssertRejected(ServerActionMessage result, string reason)
    {
        Assert.False(result.Result.Accepted);
        Assert.Contains(reason, result.Result.Rejection!.Reason, StringComparison.Ordinal);
    }

    private static int Count(PlayerInventorySnapshot inventory, string kind) =>
        inventory.AllItems().Count(item => item.ItemKind == kind);

    private static long WalletRow(MigrationTestDatabase db)
        => Convert.ToInt64(db.Scalar($"SELECT COALESCE(SUM(gold), 0) FROM player_wallets WHERE player_name = '{CharacterKey}'"));

    private static long ItemRows(MigrationTestDatabase db, string kind)
        => Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM item_instances WHERE owner_player_name = '{CharacterKey}' AND item_kind = '{kind}'"));

    private static NpcStockEntry TorchStock() =>
        Assert.Single(ContentCatalog.Shipped.Npcs[0].Stock, entry => entry.ItemKind == Torch);

    [Fact]
    public async Task AKillPaysGold_TheVendorReplicates_AndEveryTradeIsCheckedByTheServer_AndTheGoldSurvivesARestart()
    {
        using MigrationTestDatabase db = new();
        long goldAtTheEnd;
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session);
            await StepAsync(server, client);
            IAuthoritativePlayerTarget target = Target(server);
            Assert.True(Vector3.Distance(target.Position, Spawn) < 1f, $"joined at {target.Position}");
            Assert.Equal(0L, client.Inventory!.Value.Gold);

            // The vendor is an actor of kind Npc, with the content's name and look, near the spawn.
            NpcDefinition vendor = ContentCatalog.Shipped.Npcs[0];
            ActorSnapshot npc = client.Applied.OfType<ServerSnapshotMessage>()
                .SelectMany(snapshot => snapshot.Snapshot.Actors)
                .First(actor => actor.Kind == CreatureKind.Npc);
            Assert.Equal(ReplicatedActorIdRanges.FirstNpcId, npc.ActorId);
            Assert.Equal(vendor.DisplayName, npc.DisplayName);
            Assert.Equal(vendor.VisualKey, npc.VisualKey);
            Assert.Equal((1, 1), (npc.CurrentHealth, npc.MaxHealth));
            Assert.True(VendorRules.InRange(target.Position, npc.Position));
            int npcId = npc.ActorId;

            // A kill pays gold: the push carries a "<n> gold" notice and the wallet in its snapshot.
            string killed = "";
            for (int i = 0; i < 128 && client.Inventory!.Value.Gold == 0; i++)
            {
                EnemyActor enemy = server.Runtime.Enemies.ActiveActors.First(e => e.IsActive);
                killed = enemy.EnemyKind;
                enemy.SetPosition(target.EyePosition + (target.Facing * 1f) - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f));
                enemy.Combatant.Health.SetAuthoritativeState(1, enemy.Combatant.Health.MaxHealth);
                await StepAsync(server, client, attack: true, except: enemy);
            }

            long paid = client.Inventory!.Value.Gold;
            Assert.True(ContentCatalog.Shipped.TryGetEnemy(killed, out EnemyTemplate? template));
            Assert.InRange(paid, template.GoldMin, template.GoldMax);
            Assert.Equal(paid, server.Runtime.InventoryService.GoldOf(CharacterKey));
            Assert.Contains(
                client.Applied.OfType<InventoryPushMessage>().SelectMany(push => push.Notices),
                notice => notice.ItemDisplayName.EndsWith(" gold", StringComparison.Ordinal));

            // Out of range: the window does not open, a buy and a sell fail, and nothing changes.
            target.SetWorldPosition(FarFromTheVendor);
            AssertRejected(await ActAsync(server, client, new NpcInteractIntent(npcId)), "too far");
            AssertRejected(await ActAsync(server, client, new VendorBuyIntent(npcId, Torch)), "too far");
            Assert.Equal(paid, server.Runtime.InventoryService.GoldOf(CharacterKey));
            target.SetWorldPosition(Spawn);

            // In range: the window opens with the content's stock and prices.
            ServerActionMessage opened = await ActAsync(server, client, new NpcInteractIntent(npcId));
            Assert.True(opened.Result.Accepted, opened.Result.Rejection?.Reason);
            VendorOpenedResult window = Assert.IsType<VendorOpenedResult>(opened.Result.Payload);
            Assert.Equal(npcId, window.NpcActorId);
            Assert.Equal(vendor.DisplayName, window.DisplayName);
            Assert.Equal(vendor.Stock.Select(s => (s.ItemKind, s.Price)), window.Stock.Select(s => (s.ItemKind, s.Price)));
            AssertRejected(await ActAsync(server, client, new NpcInteractIntent(npcId + 1)), "too far");

            // Too little gold, and not in stock: rejected, gold and bag unchanged.
            NpcStockEntry dear = vendor.Stock.OrderByDescending(s => s.Price).First();
            long held = server.Runtime.InventoryService.GoldOf(CharacterKey);
            Assert.True(held < dear.Price, "the kill must not already pay for the dearest item");
            int itemsBefore = server.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(Session))!.Value.AllItems().Count();
            AssertRejected(await ActAsync(server, client, new VendorBuyIntent(npcId, dear.ItemKind)), "not enough gold");
            AssertRejected(await ActAsync(server, client, new VendorBuyIntent(npcId, "gear.iron-blade")), "does not sell");
            Assert.Equal(held, server.Runtime.InventoryService.GoldOf(CharacterKey));
            Assert.Equal(itemsBefore, server.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(Session))!.Value.AllItems().Count());

            // Fund the wallet (a server-side credit, as a kill would), then buy a torch.
            Assert.True(server.Runtime.InventoryService.TryAddGold(CharacterKey, 100));
            long beforeBuy = server.Runtime.InventoryService.GoldOf(CharacterKey);
            int torches = Count(client.Inventory!.Value, Torch);
            ServerActionMessage bought = await ActAsync(server, client, new VendorBuyIntent(npcId, Torch));
            Assert.True(bought.Result.Accepted, bought.Result.Rejection?.Reason);
            Assert.IsType<InventoryChangedResult>(bought.Result.Payload);
            long price = TorchStock().Price;
            Assert.Equal(beforeBuy - price, client.Inventory!.Value.Gold);
            Assert.Equal(torches + 1, Count(client.Inventory.Value, Torch));

            // Sell it back: it pays less than it cost, so a round trip never makes gold.
            InventorySlotSnapshot torch = client.Inventory.Value.Slots
                .Where(slot => slot.Item?.ItemKind == Torch)
                .OrderByDescending(slot => slot.Item!.Value.ItemInstanceId)
                .First();
            VendorSellIntent sell = new(npcId, torch.SlotIndex, torch.Item!.Value.ItemInstanceId);
            ServerActionMessage sold = await ActAsync(server, client, sell);
            Assert.True(sold.Result.Accepted, sold.Result.Rejection?.Reason);
            long afterSell = client.Inventory!.Value.Gold;
            Assert.True(afterSell > beforeBuy - price);
            Assert.True(afterSell < beforeBuy, $"{beforeBuy} gold became {afterSell}: a buy and sell must lose gold");
            Assert.Equal(torches, Count(client.Inventory.Value, Torch));

            // Selling the same item again fails: the item is gone, the gold is paid once.
            AssertRejected(await ActAsync(server, client, sell), "empty");
            Assert.Equal(afterSell, server.Runtime.InventoryService.GoldOf(CharacterKey));
            Assert.Equal(afterSell, client.Inventory!.Value.Gold);

            // A worn or invented item cannot be sold either.
            AssertRejected(await ActAsync(server, client, new VendorSellIntent(npcId, 100, 1)), "slot index");

            await PersistenceFlush.AwaitAsync(server.Runtime.PersistenceWrites);
            Assert.Equal(0, server.Runtime.PersistenceWrites!.FailedWrites);
            goldAtTheEnd = afterSell;
            Assert.Equal(goldAtTheEnd, WalletRow(db));
            await client.LeaveAsync();
        }

        // A restart: the wallet row is in the database and the rejoining client is told it.
        SqlitePools.Release(db.Schema);
        await using WireLoopbackServer again = await StartAsync(db);
        await using HeadlessPlayScene back = await HeadlessPlayScene.JoinAsync(again, Session);
        Assert.Equal(goldAtTheEnd, back.Inventory!.Value.Gold);
        Assert.Equal(goldAtTheEnd, again.Runtime.InventoryService.GoldOf(CharacterKey));
        await back.LeaveAsync();
    }

    [Fact]
    public async Task AWriteThatFails_LeavesGoldAndItemTogether_InTheRowsAndInTheScene()
    {
        using MigrationTestDatabase db = new();
        long goldInRows;
        int torchRows;
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session);
            await StepAsync(server, client);
            Assert.True(server.Runtime.InventoryService.TryAddGold(CharacterKey, 100));
            await PersistenceFlush.AwaitAsync(server.Runtime.PersistenceWrites);
            Assert.Equal(100L, WalletRow(db));
            goldInRows = WalletRow(db);
            torchRows = (int)ItemRows(db, Torch);
            long failedBefore = server.Runtime.PersistenceWrites!.FailedWrites;

            // Every wallet update now aborts, so the buy's one closure rolls back as a whole.
            db.ExecuteScript("CREATE TRIGGER fail_wallet BEFORE UPDATE ON player_wallets BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
            int torches = Count(client.Inventory!.Value, Torch);
            ServerActionMessage bought = await ActAsync(server, client, new VendorBuyIntent(ReplicatedActorIdRanges.FirstNpcId, Torch));
            Assert.True(bought.Result.Accepted, bought.Result.Rejection?.Reason);
            await PersistenceFlush.AwaitAsync(server.Runtime.PersistenceWrites);
            Assert.Equal(failedBefore + 1, server.Runtime.PersistenceWrites.FailedWrites);

            // The scene saw the item and the debit together (the session keeps its inventory) ...
            Assert.Equal(torches + 1, Count(client.Inventory!.Value, Torch));
            Assert.Equal(100 - TorchStock().Price, client.Inventory.Value.Gold);
            // ... and the rows have neither: no new item, the old gold.
            Assert.Equal(goldInRows, WalletRow(db));
            Assert.Equal(torchRows, ItemRows(db, Torch));
            await client.LeaveAsync();
        }

        // After a restart the character has the rows' state: the old gold and no extra item. Nothing was
        // duplicated and no gold was lost without the item to show for it.
        SqlitePools.Release(db.Schema);
        db.ExecuteScript("DROP TRIGGER fail_wallet;");
        await using WireLoopbackServer again = await StartAsync(db);
        await using HeadlessPlayScene back = await HeadlessPlayScene.JoinAsync(again, Session);
        Assert.Equal(goldInRows, back.Inventory!.Value.Gold);
        Assert.Equal(torchRows, Count(back.Inventory.Value, Torch));
        await back.LeaveAsync();
    }
}
