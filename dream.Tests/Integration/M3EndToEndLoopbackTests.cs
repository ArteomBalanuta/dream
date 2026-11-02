using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
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

// M3 end to end, with nothing stubbed on the way: two real PlayScenes (no GL) over real TCP on a real
// SQLite database, the shipped enemies, camps and vendor (EnemyContent = ContentCatalog.Shipped, the NPCs
// spawned as the production host does). The only fixture is the loot table: a sure one-band drop of a
// shipped item, so a kill always has something to put on the ground (the shipped wolf table pays nothing
// three kills in four). A real attack command kills a real enemy; the item lands at its real death
// position with the killer's claim; it reaches both scenes; the stranger is refused; the winner picks it
// up with the E key (PlayScene.HandleInteractKey); the kill gold shows in the scene's inventory; E on the
// vendor opens the window; a buy and a sell go through the HUD request path; a restart and a rejoin find
// the same gold and items, once.
//
// Why a WireLoopbackClient's action sequences go stale after attacks (asked to find out): the harness
// keeps one counter for commands and a separate one for actions. The server spends the accepted-action
// sequence on every primary use it synthesizes from a held attack command (it advances it to the command's
// sequence, WorldSessionRuntime.ProcessAuthoritativeAction), so after N attack commands every client
// action with a sequence at or below N is rejected as stale. The real client does not have this problem:
// ClientActionSequenceTracker is one counter, advanced past every command it sends
// (PlayScene.RunPredictionTick) and every result it receives. So it is a harness property, not a client
// bug; this class drives the scene's own tracker the way Update does.
[Collection(TimingSensitiveCollection.Name)]
public sealed class M3EndToEndLoopbackTests
{
    private const string SessionA = "m3e2e-a";
    private const string SessionB = "m3e2e-b";
    private const string Band = "gear.iron-band";
    private const string Torch = "tool.torch";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly Vector3 Pen = new(45f, 1f, 45f);
    private static readonly Vector3 SpotA = new(20f, 1.5f, 20f);
    private static readonly Vector3 SpotB = new(22f, 1.5f, 20f);
    private static readonly Vector3 NearTheVendor = new(5f, 1.5f, 5f);

    private static string KeyOf(string session) => WorldSessionRuntime.ResolveJoinPlayerName(session, $"Name {session}");

    // The shipped items and enemies with one sure table: every kill drops one common iron band.
    private static ContentCatalog SureBand()
    {
        string dir = ContentCatalog.ShippedDirectory;
        const string Table = "\",\"drops\":1,\"nothingWeight\":0,\"items\":[{\"item\":\"" + Band + "\",\"weight\":1}],\"rarity\":{\"common\":1,\"uncommon\":0,\"rare\":0}}";
        return ContentCatalog.Load(
            File.ReadAllText(Path.Combine(dir, ContentCatalog.ItemsFileName)),
            File.ReadAllText(Path.Combine(dir, ContentCatalog.EnemiesFileName)),
            "{\"version\":1,\"tables\":[{\"key\":\"wolf" + Table + ",{\"key\":\"dire-wolf" + Table + "]}");
    }

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        server.Runtime.EnemyContent = ContentCatalog.Shipped;
        server.Runtime.SpawnContentNpcs();
        server.Runtime.LootContent = SureBand();
        return server;
    }

    private static IAuthoritativePlayerTarget Target(WireLoopbackServer server, string session) =>
        WaveIRuntimeKit.Target(server.Runtime, session);

    // ---- driving the scene the way PlayScene.Update does ----

    private static ulong CommandSequenceOf(HeadlessPlayScene client) =>
        (ulong)typeof(HeadlessPlayScene).GetField("_commandSequence", Fields)!.GetValue(client)!;

    private static ClientActionSequenceTracker Tracker(HeadlessPlayScene client) =>
        (ClientActionSequenceTracker)typeof(PlayScene).GetField("_actionSequences", Fields)!.GetValue(client.Scene)!;

    // The sequence the scene's next action will use.
    private static ulong NextActionSequence(HeadlessPlayScene client) =>
        (ulong)typeof(ClientActionSequenceTracker).GetField("_nextActionSequence", Fields)!.GetValue(Tracker(client))!;

    private static KeyboardState Keyboard(params Keys[] down)
    {
        KeyboardState state = (KeyboardState)Activator.CreateInstance(typeof(KeyboardState), nonPublic: true)!;
        // OpenTK's own window fills the state through an internal setter; its arity differs between versions.
        MethodInfo set = typeof(KeyboardState).GetMethods(Fields).Single(m => m.Name == "SetKeyState");
        foreach (Keys key in down)
        {
            object[] args = set.GetParameters().Length == 3 ? [key, 0, true] : [key, true];
            set.Invoke(state, args);
        }

        return state;
    }

    // One frame with E held, one with nothing: the edge PlayScene.HandleInteractKey fires on. The sequence
    // of the intent it sent, or null when E did nothing.
    private static ulong? PressE(HeadlessPlayScene client)
    {
        MethodInfo handle = typeof(PlayScene).GetMethod("HandleInteractKey", Fields)!;
        ulong before = NextActionSequence(client);
        handle.Invoke(client.Scene, [Keyboard(Keys.E)]);
        handle.Invoke(client.Scene, [Keyboard()]);
        return NextActionSequence(client) > before ? before : null;
    }

    // A HUD pick, as the classic HUD hands it to the scene. The sequence of the intent, or null.
    private static ulong? Request(HeadlessPlayScene client, HudInteractionRequest request)
    {
        ulong before = NextActionSequence(client);
        client.Scene.ApplyEconomyRequest(request);
        return NextActionSequence(client) > before ? before : null;
    }

    private static int LocalActorId(HeadlessPlayScene client) =>
        (int)typeof(PlayScene).GetProperty("LocalActorId", Fields)!.GetValue(client.Scene)!;

    private static void ReleaseE(HeadlessPlayScene client) =>
        typeof(PlayScene).GetMethod("HandleInteractKey", Fields)!.Invoke(client.Scene, [Keyboard()]);

    // Teleports the server's player and the scene's own: Update (prediction) does not run in the headless
    // scene, so nothing would carry the scene's player to where the server put it.
    private static async Task PlaceAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who, Vector3 position)
    {
        Target(server, who.SessionId).SetWorldPosition(position);
        who.World.Player.SetWorldPosition(position);
        await StepAsync(server, clients);
        await StepAsync(server, clients);
        Assert.True(Vector3.Distance(who.World.Player.GetWorldPosition(), position) < 1f, $"{who.SessionId} drifted to {who.World.Player.GetWorldPosition()}");
    }

    // ---- stepping ----

    // One command per scene (the attacker's holds the attack), one tick, and every scene has applied that
    // tick's snapshot. Enemies are kept out of the way, except the one given.
    private static async Task StepAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene? attacker = null, EnemyActor? except = null)
    {
        foreach (EnemyActor enemy in server.Runtime.Enemies.ActiveActors)
        {
            if (!ReferenceEquals(enemy, except))
            {
                enemy.SetPosition(Pen);
            }
        }

        foreach (HeadlessPlayScene client in clients)
        {
            await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, ReferenceEquals(client, attacker));
            Tracker(client).ObserveCommandSequence(CommandSequenceOf(client));
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

    private static async Task WaitActionReadAsync(WireLoopbackServer server, HeadlessPlayScene client, ulong sequence)
    {
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (!server.Host.TryGetConnectionContext(client.Connection.ConnectionId, out ServerRoutingContext? context)
            || context?.LastActionMessage?.ActionSequence < sequence
            || context?.LastActionMessage == null)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The server never read {client.SessionId}'s action {sequence}.");
            await Task.Delay(1);
        }
    }

    // The result of an action the scene sent, once the server has read it and ticked.
    private static async Task<ServerActionMessage> ResultAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who, ulong? sequence)
    {
        Assert.NotNull(sequence);
        await WaitActionReadAsync(server, who, sequence.Value);
        for (int i = 0; i < 8; i++)
        {
            await StepAsync(server, clients);
            who.Pump();
            if (who.Applied.OfType<ServerActionMessage>().FirstOrDefault(m => m.Result.ActionSequence == sequence && m.Result.SessionId == who.SessionId) is { } result)
            {
                return result;
            }
        }

        throw new Xunit.Sdk.XunitException($"no result for {who.SessionId}'s action {sequence}");
    }

    private static void AssertRejected(ServerActionMessage result, string reason)
    {
        Assert.False(result.Result.Accepted, "the action was accepted");
        Assert.Contains(reason, result.Result.Rejection!.Reason, StringComparison.Ordinal);
    }

    // ---- the kill ----

    private sealed record Kill(EnemyActor Enemy, Vector3 Placed);

    // Holds the attack on a 1 HP enemy in front of the killer until it dies: a real attack command, the
    // real resolver, real loot and gold.
    private static async Task<Kill> KillAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene killer)
    {
        IAuthoritativePlayerTarget target = Target(server, killer.SessionId);
        for (int i = 0; i < 96; i++)
        {
            EnemyActor? enemy = server.Runtime.Enemies.ActiveActors.FirstOrDefault(e => e.IsActive && e.Combatant.Health.IsAlive);
            Vector3 placed = default;
            if (enemy != null)
            {
                enemy.SetPosition(target.EyePosition + (target.Facing * 2f) - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f));
                enemy.Combatant.Health.SetAuthoritativeState(1, enemy.Combatant.Health.MaxHealth);
                placed = enemy.Position;
            }

            await StepAsync(server, clients, killer, enemy);
            if (enemy != null && !enemy.Combatant.Health.IsAlive)
            {
                return new Kill(enemy, placed);
            }
        }

        throw new Xunit.Sdk.XunitException("no enemy could be killed");
    }

    private static GroundItemSnapshot[] ServerGround(WireLoopbackServer server)
    {
        List<GroundItemSnapshot> all = [];
        server.Runtime.CaptureGroundItems(all);
        return [.. all];
    }

    private static long[] ClientGroundIds(HeadlessPlayScene client)
    {
        client.Pump();
        List<long> ids = [];
        for (int i = 0; i < client.GroundItems.Count; i++)
        {
            ids.Add(client.GroundItems[i].ItemInstanceId);
        }

        ids.Sort();
        return [.. ids];
    }

    private static Task GroundSettledAsync(HeadlessPlayScene client, int count) =>
        client.PumpUntilAsync(c => c.GroundItems.Count == count, $"{count} ground items");

    private static PlayerInventorySnapshot InventoryOf(WireLoopbackServer server, string session) =>
        server.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(session))!.Value;

    private static void Flush(WireLoopbackServer server)
    {
        Assert.True(server.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, server.Runtime.PersistenceWrites.FailedWrites);
    }

    private static long Count(MigrationTestDatabase db, string sql) => Convert.ToInt64(db.Scalar(sql));

    private static async Task<(HeadlessPlayScene A, HeadlessPlayScene B)> JoinBothAsync(WireLoopbackServer server)
    {
        HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        HeadlessPlayScene b = await HeadlessPlayScene.JoinAsync(server, SessionB);
        await StepAsync(server, [a, b]);
        await PlaceAsync(server, [a, b], a, SpotA);
        await PlaceAsync(server, [a, b], b, SpotB);
        return (a, b);
    }

    // ---- the tests ----

    [Fact]
    public async Task ARealKill_DropsAtTheDeathPosition_ClaimBlocksTheStranger_TheWinnerPressesE_TheVendorTrades_AndARestartIsConsistent()
    {
        using MigrationTestDatabase db = new();
        string keyA = KeyOf(SessionA);
        long groundId;
        long goldAtTheEnd;
        long[] itemIdsAtTheEnd;
        long torchesAtTheEnd;

        await using (WireLoopbackServer server = await StartAsync(db))
        {
            (HeadlessPlayScene a, HeadlessPlayScene b) = await JoinBothAsync(server);
            await using HeadlessPlayScene clientA = a;
            await using HeadlessPlayScene clientB = b;
            HeadlessPlayScene[] both = [a, b];
            WorldSessionRuntime runtime = server.Runtime;
            IAuthoritativePlayerTarget targetA = Target(server, SessionA);
            IAuthoritativePlayerTarget targetB = Target(server, SessionB);

            // Both players stand where the scene was told, away from the vendor; the vendor is known to both.
            Assert.True(Vector3.Distance(a.World.Player.GetWorldPosition(), targetA.Position) < 1f, $"A: {a.World.Player.GetWorldPosition()} vs {targetA.Position}");
            Assert.True(Vector3.Distance(b.World.Player.GetWorldPosition(), targetB.Position) < 1f, $"B: {b.World.Player.GetWorldPosition()} vs {targetB.Position}");
            Assert.Equal(2, a.Npcs.Count);
            Assert.Equal(2, b.Npcs.Count);
            Assert.False(a.Npcs.TryGetNearest(a.World.Player.GetWorldPosition(), ClientNpcCache.InteractRange, out _));
            Assert.Equal(0, a.GroundItems.Count);
            Assert.Equal(0L, a.Inventory!.Value.Gold);

            // ---- a real kill with a full bag: the item cannot go to the bag, so it lands on the ground ----
            runtime.InventoryService.FillBagLeaving(keyA, freeCells: 0);
            Assert.Equal(0, InventoryOf(server, SessionA).FreeCells());
            Kill kill = await KillAsync(server, both, a);
            await a.PumpUntilAsync(c => c.Inventory!.Value.Gold > 0, "the kill gold");

            Assert.True(ContentCatalog.Shipped.TryGetEnemy(kill.Enemy.EnemyKind, out EnemyTemplate? template));
            long killGold = a.Inventory!.Value.Gold;
            Assert.InRange(killGold, template.GoldMin, template.GoldMax);
            Assert.Equal(killGold, runtime.InventoryService.GoldOf(keyA));
            Assert.Equal(0L, runtime.InventoryService.GoldOf(KeyOf(SessionB)));
            Assert.Contains(
                a.Applied.OfType<InventoryPushMessage>().SelectMany(push => push.Notices),
                notice => notice.ItemDisplayName.EndsWith(" gold", StringComparison.Ordinal));

            // The item is on the ground at the real death position (the ring's first point), claimed by the killer.
            Assert.Equal(1, runtime.GroundItemCount);
            GroundItemSnapshot onGround = Assert.Single(ServerGround(server));
            groundId = onGround.ItemInstanceId;
            Assert.Equal(Band, onGround.ItemKind);
            Assert.True(Vector3.Distance(onGround.Position, kill.Enemy.Position + new Vector3(0.7f, 0f, 0f)) < 0.05f, $"{onGround.Position} vs death at {kill.Enemy.Position}");
            Assert.True(Vector3.Distance(onGround.Position, kill.Placed) < 1.5f, $"{onGround.Position} vs placed at {kill.Placed}");
            Assert.Equal(targetA.ActorId, onGround.ClaimantActorId);
            Assert.True(onGround.ClaimExpiresTick > runtime.CurrentServerTick);
            Assert.Equal(0, InventoryOf(server, SessionA).FreeCells());
            Assert.DoesNotContain(InventoryOf(server, SessionA).AllItems(), item => item.ItemInstanceId == groundId);

            // It reaches both scenes with the same facts.
            await GroundSettledAsync(a, 1);
            await GroundSettledAsync(b, 1);
            foreach (HeadlessPlayScene scene in both)
            {
                Assert.True(scene.GroundItems.TryGet(groundId, out ClientGroundItem seen));
                Assert.Equal(Band, seen.ItemKind);
                Assert.Equal(targetA.ActorId, seen.ClaimantActorId);
                Assert.Equal(onGround.ClaimExpiresTick, seen.ClaimExpiresTick);
                Assert.True(Vector3.Distance(seen.Position, onGround.Position) < 0.01f);
                Assert.False(string.IsNullOrWhiteSpace(seen.DisplayName));
            }

            Flush(server);
            Assert.Equal(1, Count(db, "SELECT COUNT(*) FROM item_instances WHERE state = 'Ground' AND owner_player_name IS NULL"));

            // ---- the claim blocks the stranger: whatever her E does, the server refuses; so does a forced intent ----
            // (The scene's own decision to offer the pickup is pinned apart, in
            // TheClientJudgesAClaimByTheServersActorId_*: B's scene wrongly thinks she holds this claim.)
            if (PressE(b) is { } strangerKey)
            {
                AssertRejected(await ResultAsync(server, both, b, strangerKey), "reserved for another player");
            }

            ulong? forced = Request(b, HudInteractionRequest.None with { GroundPickupId = groundId });
            AssertRejected(await ResultAsync(server, both, b, forced), "reserved for another player");
            Assert.Equal(1, runtime.GroundItemCount);
            Assert.DoesNotContain(InventoryOf(server, SessionB).AllItems(), item => item.ItemInstanceId == groundId);

            // ---- the winner presses E with a full bag: the server says so; with room, E picks it up ----
            ulong? full = PressE(a);
            Assert.NotNull(full);
            AssertRejected(await ResultAsync(server, both, a, full), "backpack is full");
            Assert.Equal(1, runtime.GroundItemCount);

            InventorySlotSnapshot band = InventoryOf(server, SessionA).BagItems().First(slot => slot.Item!.Value.ItemKind == Band);
            Assert.Equal(InventoryEquipOutcome.Equipped, runtime.InventoryService.TryDestroy(keyA, band.SlotIndex, band.Item!.Value.ItemInstanceId));
            ulong? pickup = PressE(a);
            Assert.NotNull(pickup);
            ServerActionMessage picked = await ResultAsync(server, both, a, pickup);
            Assert.True(picked.Result.Accepted, picked.Result.Rejection?.Reason);
            Assert.IsType<InventoryChangedResult>(picked.Result.Payload);
            Assert.Single(a.Inventory!.Value.AllItems(), item => item.ItemInstanceId == groundId);
            Assert.Single(InventoryOf(server, SessionA).AllItems(), item => item.ItemInstanceId == groundId);
            Assert.Equal(killGold, a.Inventory.Value.Gold);
            Assert.Equal(0, runtime.GroundItemCount);
            await GroundSettledAsync(a, 0);
            await GroundSettledAsync(b, 0);

            // E again with nothing in reach does nothing (no second pickup of the same item).
            Assert.Null(PressE(a));
            Flush(server);
            Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM item_instances WHERE state = 'Ground'"));
            Assert.Equal(1, Count(db, $"SELECT COUNT(*) FROM player_inventory_slots WHERE item_instance_id = {groundId}"));

            // ---- the vendor: E opens the window ----
            await PlaceAsync(server, both, a, NearTheVendor);
            NpcDefinition vendor = ContentCatalog.Shipped.Npcs[0];
            Assert.True(a.Npcs.TryGetNearest(a.World.Player.GetWorldPosition(), ClientNpcCache.InteractRange, out ClientNpc npc), $"A at {a.World.Player.GetWorldPosition()}");
            Assert.Equal(ReplicatedActorIdRanges.FirstNpcId, npc.ActorId);
            Assert.False(a.Vendor.IsOpen);
            ulong? talk = PressE(a);
            ServerActionMessage opened = await ResultAsync(server, both, a, talk);
            Assert.True(opened.Result.Accepted, opened.Result.Rejection?.Reason);
            Assert.IsType<VendorOpenedResult>(opened.Result.Payload);
            Assert.True(a.Vendor.IsOpen);
            Assert.Equal(npc.ActorId, a.Vendor.NpcActorId);
            Assert.Equal(vendor.Stock.Count, a.Vendor.StockCount);
            for (int i = 0; i < a.Vendor.StockCount; i++)
            {
                Assert.Equal(vendor.Stock[i].ItemKind, a.Vendor.StockAt(i).ItemKind);
                Assert.Equal(vendor.Stock[i].Price, a.Vendor.StockAt(i).Price);
            }

            Assert.True(a.Vendor.SellsKind(Torch));

            // Fund the wallet (a server-side credit) and make room for the 1x2 torch: two bands one above the other.
            long topUp = 20;
            Assert.True(runtime.InventoryService.TryAddGold(keyA, topUp));
            long heldBeforeBuy = killGold + topUp;
            List<InventorySlotSnapshot> bands = [.. InventoryOf(server, SessionA).BagItems().Where(slot => slot.Item!.Value.ItemKind == Band && slot.Item!.Value.ItemInstanceId != groundId)];
            InventorySlotSnapshot upper = bands.First(s => bands.Any(o => o.SlotIndex == s.SlotIndex + BagGrid.Columns));  // never the picked-up band
            InventorySlotSnapshot lower = bands.First(s => s.SlotIndex == upper.SlotIndex + BagGrid.Columns);
            Assert.Equal(InventoryEquipOutcome.Equipped, runtime.InventoryService.TryDestroy(keyA, upper.SlotIndex, upper.Item!.Value.ItemInstanceId));
            Assert.Equal(InventoryEquipOutcome.Equipped, runtime.InventoryService.TryDestroy(keyA, lower.SlotIndex, lower.Item!.Value.ItemInstanceId));

            long price = Assert.Single(vendor.Stock, entry => entry.ItemKind == Torch).Price;
            long torchesBefore = a.Inventory!.Value.AllItems().Count(item => item.ItemKind == Torch);
            ulong? buy = Request(a, HudInteractionRequest.None with { Vendor = new VendorHudAction(VendorHudActionKind.Buy, Torch) });
            ServerActionMessage bought = await ResultAsync(server, both, a, buy);
            Assert.True(bought.Result.Accepted, bought.Result.Rejection?.Reason);
            Assert.Equal(heldBeforeBuy - price, a.Inventory!.Value.Gold);
            Assert.Equal(heldBeforeBuy - price, runtime.InventoryService.GoldOf(keyA));
            Assert.Equal(torchesBefore + 1, a.Inventory.Value.AllItems().Count(item => item.ItemKind == Torch));
            InventorySlotSnapshot torch = a.Inventory.Value.BagItems().Where(slot => slot.Item!.Value.ItemKind == Torch).OrderByDescending(slot => slot.Item!.Value.ItemInstanceId).First();

            ulong? sell = Request(a, HudInteractionRequest.None with
            {
                Vendor = new VendorHudAction(VendorHudActionKind.Sell, SlotIndex: torch.SlotIndex, ItemInstanceId: torch.Item!.Value.ItemInstanceId),
            });
            ServerActionMessage sold = await ResultAsync(server, both, a, sell);
            Assert.True(sold.Result.Accepted, sold.Result.Rejection?.Reason);
            long afterSell = a.Inventory.Value.Gold;
            Assert.True(afterSell > heldBeforeBuy - price);
            Assert.True(afterSell < heldBeforeBuy, "a buy and a sell must lose gold");
            Assert.Equal(afterSell, runtime.InventoryService.GoldOf(keyA));
            Assert.Equal(torchesBefore, a.Inventory.Value.AllItems().Count(item => item.ItemKind == Torch));
            Assert.DoesNotContain(a.Inventory.Value.AllItems(), item => item.ItemInstanceId == torch.Item!.Value.ItemInstanceId);

            // Walking away closes the window.
            await PlaceAsync(server, both, a, SpotA);
            ReleaseE(a);
            Assert.False(a.Vendor.IsOpen);

            // ---- what the server holds, then a restart ----
            Flush(server);
            goldAtTheEnd = afterSell;
            Assert.Equal(goldAtTheEnd, Count(db, $"SELECT COALESCE(SUM(gold), 0) FROM player_wallets WHERE player_name = '{keyA}'"));
            itemIdsAtTheEnd = [.. InventoryOf(server, SessionA).AllItems().Select(item => item.ItemInstanceId).Order()];
            torchesAtTheEnd = InventoryOf(server, SessionA).AllItems().Count(item => item.ItemKind == Torch);
            Assert.Contains(groundId, itemIdsAtTheEnd);
            Assert.Equal(itemIdsAtTheEnd.Length, itemIdsAtTheEnd.Distinct().Count());
            Assert.Equal(itemIdsAtTheEnd.Length, Count(db, $"SELECT COUNT(*) FROM player_inventory_slots WHERE player_name = '{keyA}'"));
            Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM (SELECT 1 FROM player_inventory_slots GROUP BY world_id, item_instance_id HAVING COUNT(*) > 1)"));

            await a.LeaveAsync();
            await b.LeaveAsync();
            Flush(server);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM item_instances WHERE state = 'Ground'"));
        Assert.Equal(goldAtTheEnd, Count(db, $"SELECT COALESCE(SUM(gold), 0) FROM player_wallets WHERE player_name = '{keyA}'"));

        await using (WireLoopbackServer again = await StartAsync(db))
        {
            Assert.Equal(0, again.Runtime.GroundItemCount);
            await using HeadlessPlayScene back = await HeadlessPlayScene.JoinAsync(again, SessionA);
            await StepAsync(again, [back]);
            Assert.Equal(goldAtTheEnd, back.Inventory!.Value.Gold);
            Assert.Equal(goldAtTheEnd, again.Runtime.InventoryService.GoldOf(keyA));
            long[] restored = [.. back.Inventory.Value.AllItems().Select(item => item.ItemInstanceId).Order()];
            Assert.Equal(itemIdsAtTheEnd, restored);
            Assert.Single(restored, id => id == groundId);
            Assert.Equal(torchesAtTheEnd, back.Inventory.Value.AllItems().Count(item => item.ItemKind == Torch));
            Assert.Equal(0, back.GroundItems.Count);
            Assert.Empty(back.Applied.OfType<ServerGroundItems>());

            // The picked-up item is not on the ground again, and the ids it used stay reserved.
            Assert.True(again.Runtime.NextItemInstanceId > groundId);
            await back.LeaveAsync();
        }
    }

    [Fact]
    public async Task ASecondClient_JoiningAfterAnItemIsOnTheGround_GetsItThroughTheReset_WithTheClaim_AndNothingWhenTheGroundIsEmpty()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        await StepAsync(server, [a]);
        IAuthoritativePlayerTarget targetA = Target(server, SessionA);
        await PlaceAsync(server, [a], a, SpotA);

        // Nothing on the ground: a joiner gets no ground message at all.
        await using (HeadlessPlayScene early = await HeadlessPlayScene.JoinAsync(server, SessionB))
        {
            await StepAsync(server, [a, early]);
            await StepAsync(server, [a, early]);
            Assert.Empty(early.Applied.OfType<ServerGroundItems>());
            Assert.Equal(0, early.GroundItems.Count);
            await early.LeaveAsync();
        }

        server.Runtime.InventoryService.FillBagLeaving(KeyOf(SessionA), freeCells: 0);
        Kill kill = await KillAsync(server, [a], a);
        GroundItemSnapshot onGround = Assert.Single(ServerGround(server));
        Assert.True(Vector3.Distance(onGround.Position, kill.Enemy.Position + new Vector3(0.7f, 0f, 0f)) < 0.05f);
        await GroundSettledAsync(a, 1);

        // A second client joins now: the item reaches it through a Reset, with the killer's claim.
        await using HeadlessPlayScene late = await HeadlessPlayScene.JoinAsync(server, SessionB);
        await late.PumpUntilAsync(c => c.GroundItems.Count == 1, "the Reset with the item");
        await StepAsync(server, [a, late]);
        ServerGroundItems first = late.Applied.OfType<ServerGroundItems>().First();
        Assert.True(first.Reset);
        Assert.Equal(onGround.ItemInstanceId, Assert.Single(first.Upserts).ItemInstanceId);
        Assert.Empty(first.RemovedIds);
        Assert.True(late.GroundItems.TryGet(onGround.ItemInstanceId, out ClientGroundItem seen));
        Assert.Equal(Band, seen.ItemKind);
        Assert.Equal(targetA.ActorId, seen.ClaimantActorId);
        Assert.Equal(onGround.ClaimExpiresTick, seen.ClaimExpiresTick);
        Assert.True(Vector3.Distance(seen.Position, onGround.Position) < 0.01f);
        Assert.Equal(ClientGroundIds(a), ClientGroundIds(late));

        // The item is then picked up: the late joiner's list follows, and a rejoin starts empty.
        PlayerInventorySnapshot bag = InventoryOf(server, SessionA);
        InventorySlotSnapshot band = bag.BagItems().First(slot => slot.Item!.Value.ItemKind == Band);
        Assert.Equal(InventoryEquipOutcome.Equipped, server.Runtime.InventoryService.TryDestroy(KeyOf(SessionA), band.SlotIndex, band.Item!.Value.ItemInstanceId));
        ulong? pickup = PressE(a);
        ServerActionMessage picked = await ResultAsync(server, [a, late], a, pickup);
        Assert.True(picked.Result.Accepted, picked.Result.Rejection?.Reason);
        await GroundSettledAsync(late, 0);
        await GroundSettledAsync(a, 0);
        Assert.Equal(0, server.Runtime.GroundItemCount);

        await late.LeaveAsync();
        await using HeadlessPlayScene again = await HeadlessPlayScene.JoinAsync(server, SessionB);
        await StepAsync(server, [a, again]);
        await StepAsync(server, [a, again]);
        Assert.Equal(0, again.GroundItems.Count);
        Assert.Empty(again.Applied.OfType<ServerGroundItems>());
        await again.LeaveAsync();
        await a.LeaveAsync();
    }

    // BUG PIN (client). PlayScene.LocalActorId is _runtime.Player.Combatant.Actor.Id, an id from the
    // client's own registry (1 on every client), but a ground item's ClaimantActorId is the SERVER's actor
    // id of the killer. The client compares the two (ClientGroundItemCache.IsHeldBack), so only the player
    // whose server actor id happens to be 1 (the first to join) judges claims right. The second player's
    // own claimed drop is "held back" from her: E does nothing for her until the claim lapses (30 s).
    [Fact]
    public async Task TheClientJudgesAClaimByTheServersActorId_TheSecondPlayersOwnDropIsOfferedToHerByE()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        (HeadlessPlayScene a, HeadlessPlayScene b) = await JoinBothAsync(server);
        await using HeadlessPlayScene clientA = a;
        await using HeadlessPlayScene clientB = b;
        HeadlessPlayScene[] both = [a, b];
        IAuthoritativePlayerTarget targetB = Target(server, SessionB);

        server.Runtime.InventoryService.FillBagLeaving(KeyOf(SessionB), freeCells: 0);
        await KillAsync(server, both, b);
        GroundItemSnapshot onGround = Assert.Single(ServerGround(server));
        Assert.Equal(targetB.ActorId, onGround.ClaimantActorId);
        await GroundSettledAsync(b, 1);

        // B is the claimant and stands next to the item: her E must send the pickup (the server will say
        // "backpack is full", which is fine; the point is that the scene asks).
        Assert.True(b.GroundItems.TryGet(onGround.ItemInstanceId, out ClientGroundItem item));
        Assert.Equal(targetB.ActorId, item.ClaimantActorId);
        Assert.True(
            targetB.ActorId == LocalActorId(b),
            $"B's scene thinks her actor id is {LocalActorId(b)}, the server's is {targetB.ActorId}: her own claim is held back from her");
        Assert.NotNull(PressE(b));
    }

    // The mirror: the second player is a stranger to the first player's claim, but her scene calls her
    // actor 1 as well, so she is offered the pickup (the server refuses it, so no item is lost).
    [Fact]
    public async Task TheClientJudgesAClaimByTheServersActorId_TheSecondPlayerIsNotOfferedTheFirstPlayersClaim()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        (HeadlessPlayScene a, HeadlessPlayScene b) = await JoinBothAsync(server);
        await using HeadlessPlayScene clientA = a;
        await using HeadlessPlayScene clientB = b;
        HeadlessPlayScene[] both = [a, b];
        IAuthoritativePlayerTarget targetA = Target(server, SessionA);

        server.Runtime.InventoryService.FillBagLeaving(KeyOf(SessionA), freeCells: 0);
        await KillAsync(server, both, a);
        GroundItemSnapshot onGround = Assert.Single(ServerGround(server));
        Assert.Equal(targetA.ActorId, onGround.ClaimantActorId);
        await GroundSettledAsync(b, 1);

        Assert.True(
            Target(server, SessionB).ActorId == LocalActorId(b),
            $"B's scene thinks her actor id is {LocalActorId(b)}, the server's is {Target(server, SessionB).ActorId}");
        Assert.Null(PressE(b));
    }

    // A world resync sends a full-world snapshot and no ground Reset. The ground list does not travel with
    // the snapshot: ground changes go as their own ordered messages to every session, so a resync neither
    // loses nor duplicates an item, and a pickup that lands in the same tick as the resync is still
    // removed. This pins that the missing Reset leaves nothing stale.
    [Fact]
    public async Task AWorldResync_LeavesNoStaleGroundItem_EvenWhenAPickupLandsInTheSameTick()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        (HeadlessPlayScene a, HeadlessPlayScene b) = await JoinBothAsync(server);
        await using HeadlessPlayScene clientA = a;
        await using HeadlessPlayScene clientB = b;
        HeadlessPlayScene[] both = [a, b];
        string keyA = KeyOf(SessionA);

        server.Runtime.InventoryService.FillBagLeaving(keyA, freeCells: 0);
        await KillAsync(server, both, a);
        long groundId = Assert.Single(ServerGround(server)).ItemInstanceId;
        await GroundSettledAsync(a, 1);
        await GroundSettledAsync(b, 1);

        // 1) A resync on its own: B gets the full world, its ground list is untouched and equals the server's.
        int resetsBefore = b.Applied.OfType<ServerGroundItems>().Count();
        int worldsBefore = b.Applied.OfType<ServerSnapshotMessage>().Count(m => m.Snapshot.World != null);
        await RequestResyncAsync(server, b);
        await StepUntilAsync(server, both, () => ResyncSnapshots(b) > worldsBefore, "the resync snapshot");
        Assert.Equal(resetsBefore, b.Applied.OfType<ServerGroundItems>().Count());
        Assert.Equal([groundId], ClientGroundIds(b));
        Assert.Equal([groundId], ServerGround(server).Select(item => item.ItemInstanceId).ToArray());
        Assert.Equal(1, server.Runtime.GroundItemCount);

        // 2) A resync and the pickup in one tick: B's list ends empty, as the server's.
        await Task.Delay(1700);
        InventorySlotSnapshot band = InventoryOf(server, SessionA).BagItems().First(slot => slot.Item!.Value.ItemKind == Band);
        Assert.Equal(InventoryEquipOutcome.Equipped, server.Runtime.InventoryService.TryDestroy(keyA, band.SlotIndex, band.Item!.Value.ItemInstanceId));
        int worldsBeforeSecond = ResyncSnapshots(b);
        await RequestResyncAsync(server, b);
        ulong? pickup = PressE(a);
        Assert.NotNull(pickup);
        await WaitActionReadAsync(server, a, pickup.Value);
        await StepUntilAsync(server, both, () => ResyncSnapshots(b) > worldsBeforeSecond && a.Inventory!.Value.AllItems().Any(i => i.ItemInstanceId == groundId), "the second resync and the pickup");
        Assert.Equal(0, server.Runtime.GroundItemCount);
        await GroundSettledAsync(b, 0);
        await GroundSettledAsync(a, 0);
        Assert.Empty(ClientGroundIds(b));
        Assert.Single(a.Inventory!.Value.AllItems(), item => item.ItemInstanceId == groundId);
    }

    private static int ResyncSnapshots(HeadlessPlayScene client)
    {
        client.Pump();
        return client.Applied.OfType<ServerSnapshotMessage>().Count(m => m.Snapshot.World != null);
    }

    private static async Task RequestResyncAsync(WireLoopbackServer server, HeadlessPlayScene client)
    {
        client.Session.QueueWorldResyncRequest(0);
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (!(server.Host.TryGetConnectionContext(client.Connection.ConnectionId, out ServerRoutingContext? context) && context!.WorldResyncPending))
        {
            Assert.True(DateTime.UtcNow < deadline, "the server never saw the resync request");
            await Task.Delay(1);
        }
    }

    private static async Task StepUntilAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, Func<bool> done, string what)
    {
        for (int i = 0; i < 60; i++)
        {
            await StepAsync(server, clients);
            foreach (HeadlessPlayScene client in clients)
            {
                client.Pump();
            }

            if (done())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new Xunit.Sdk.XunitException($"never reached {what}");
    }
}
