using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Inventory;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;
using Dream.Tests.Support;

namespace Dream.Tests.Integration;

// M3 workstream A over real TCP and a real SQLite database, with two wire clients. A kill's overflow
// lands on the ground (three claimed items, the killer's bag full); the claim keeps a stranger away; the
// claimant picks up once she has room; after the claim lapses two pickups of one item in one tick have
// exactly one winner; a restart restores the ground with no duplicate and no claim; an item despawns.
public sealed class GroundItemsLoopbackTests
{
    private const string SessionA = "gnd-a";
    private const string SessionB = "gnd-b";
    private const string Band = "gear.iron-band";

    private static readonly Vector3 Spot = new(20f, 1.5f, 20f);

    private static string KeyOf(string session) => WorldSessionRuntime.ResolveJoinPlayerName(session, $"Name {session}");

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath));
        WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        // Three 1x1 bands per kill: a full bag turns all three into ground items.
        server.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 3, kinds: [Band]);
        return server;
    }

    private static async Task TickAsync(WireLoopbackServer server, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }
    }

    private static IAuthoritativePlayerTarget Place(WireLoopbackServer server, string session)
    {
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(server.Runtime, session);
        target.SetWorldPosition(Spot);
        return target;
    }

    // The client's ground list as ClientGroundItemCache keeps it: replayed from every ground message
    // received, in order. Within one message Reset replaces, then upserts apply, then removals apply (the
    // production order, so an id listed in both is removed).
    private static Dictionary<long, GroundItemSnapshot> Ground(IReadOnlyList<WireFrame> frames)
    {
        Dictionary<long, GroundItemSnapshot> list = [];
        foreach (ServerGroundItems message in frames.Select(frame => frame.Decoded.Message).OfType<ServerGroundItems>())
        {
            Assert.InRange(message.Upserts.Count + message.RemovedIds.Count, 0, ServerGroundItems.MaxEntries);
            if (message.Reset)
            {
                list.Clear();
            }

            foreach (GroundItemSnapshot item in message.Upserts)
            {
                list[item.ItemInstanceId] = item;
            }

            foreach (long id in message.RemovedIds)
            {
                list.Remove(id);
            }
        }

        return list;
    }

    private static Task WaitGroundAsync(WireLoopbackClient client, Func<Dictionary<long, GroundItemSnapshot>, bool> done, string what) =>
        client.WaitForAsync(frames => done(Ground(frames)), what);

    // One action, one tick, and the result of that action.
    private static async Task<ActionResult> ActAsync(WireLoopbackServer server, WireLoopbackClient client, IPlayerActionPayload payload)
    {
        await client.SendActionAsync(payload);
        ulong sequence = client.SentActionSequence;
        await TickAsync(server);
        return await ResultAsync(client, sequence);
    }

    private static async Task<ActionResult> ResultAsync(WireLoopbackClient client, ulong sequence)
    {
        await client.WaitForAsync(
            frames => frames.Any(frame => frame.Decoded.Message is ServerActionMessage m && m.Result.ActionSequence == sequence),
            $"the result of action {sequence}");
        AuthoritativeActionResult result = client.Frames
            .Select(frame => frame.Decoded.Message)
            .OfType<ServerActionMessage>()
            .Single(m => m.Result.ActionSequence == sequence)
            .Result;
        return new ActionResult(result.Accepted, result.Rejection?.Reason ?? string.Empty, result.Payload);
    }

    private sealed record ActionResult(bool Accepted, string Reason, IActionResultPayload? Payload);

    private static async Task DestroyOneBandAsync(WireLoopbackServer server, WireLoopbackClient client, string session)
    {
        PlayerInventorySnapshot inventory = server.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(session))!.Value;
        InventorySlotSnapshot band = inventory.Slots.First(slot => slot.Item?.ItemKind == Band);
        ActionResult destroyed = await ActAsync(server, client, new InventoryDestroyIntent(band.SlotIndex, band.Item!.Value.ItemInstanceId));
        Assert.True(destroyed.Accepted, destroyed.Reason);
    }

    private static long Count(MigrationTestDatabase db, string sql) => Convert.ToInt64(db.Scalar(sql));

    private static void Flush(WireLoopbackServer server)
    {
        Assert.True(server.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, server.Runtime.PersistenceWrites.FailedWrites);
    }

    // The security property of the whole drop design, end to end: InventoryDropIntent carries no
    // position, so the server must place the item at the position IT holds for the dropper. Pinned by
    // moving the dropper somewhere no default could coincide with and asserting the replicated position
    // equals the server's own view of that player -- not the origin, not the shared Spot every
    // other test here uses, and not a value any client could have chosen.
    [Fact]
    public async Task ADrop_LandsAtTheServersOwnPositionForTheDropper_NotTheOriginNorAnyClientSuppliedPlace()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using WireLoopbackClient a = await server.ConnectAndJoinAsync(SessionA);
        await TickAsync(server, 2);
        IAuthoritativePlayerTarget targetA = WaveIRuntimeKit.Target(server.Runtime, SessionA);
        // Offset from the Spot every other test here uses, so it is well inside the map: a position
        // outside it is clamped to the world centre by the server, which would make this assert the
        // clamp rather than the drop.
        Vector3 standing = Spot + new Vector3(7.25f, 0f, 11.5f);
        targetA.SetWorldPosition(standing);
        await TickAsync(server);
        WorldSessionRuntime runtime = server.Runtime;

        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            runtime.InventoryService.TryGrantRolled(KeyOf(SessionA), Band, ItemRarity.Common, new ItemRollSeed(9, 9, 9, 9, 0)));
        await TickAsync(server);
        InventorySlotSnapshot band = runtime.TryCaptureInventorySnapshot(new PlayerSessionId(SessionA))!.Value.Slots.First(slot => slot.Item?.ItemKind == Band);
        long id = band.Item!.Value.ItemInstanceId;

        ActionResult dropped = await ActAsync(server, a, new InventoryDropIntent(band.SlotIndex, id));
        Assert.True(dropped.Accepted, dropped.Reason);
        await WaitGroundAsync(a, ground => ground.Count == 1, "the dropped item");

        GroundItemSnapshot seen = Assert.Single(Ground(a.Frames).Values);
        Assert.Equal(id, seen.ItemInstanceId);
        // The invariant: the item sits where the SERVER holds the dropper, whatever that is by now.
        // `standing` below only guards against the default positions, so a later gravity or terrain
        // snap at that spot cannot turn this into a false failure.
        Assert.Equal((Vector3)targetA.Position, seen.Position);
        Assert.NotEqual(Vector3.Zero, seen.Position);
        Assert.NotEqual(Spot, seen.Position);
    }

    [Fact]
    public async Task ADrop_BecomesAGroundUpsert_TheStrangerIsRefused_TheDropperPicksItBackUp_AndADeadPlayerCannotDrop()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using WireLoopbackClient a = await server.ConnectAndJoinAsync(SessionA);
        await using WireLoopbackClient b = await server.ConnectAndJoinAsync(SessionB);
        await TickAsync(server, 2);
        IAuthoritativePlayerTarget targetA = Place(server, SessionA);
        Place(server, SessionB);
        await TickAsync(server);
        WorldSessionRuntime runtime = server.Runtime;
        Assert.Equal(0, runtime.GroundItemCount);

        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            runtime.InventoryService.TryGrantRolled(KeyOf(SessionA), Band, ItemRarity.Common, new ItemRollSeed(7, 7, 7, 7, 0)));
        await TickAsync(server);
        InventorySlotSnapshot band = runtime.TryCaptureInventorySnapshot(new PlayerSessionId(SessionA))!.Value.Slots.First(slot => slot.Item?.ItemKind == Band);
        long id = band.Item!.Value.ItemInstanceId;
        int free = runtime.TryCaptureInventorySnapshot(new PlayerSessionId(SessionA))!.Value.FreeCells();

        // ---- the drop: the item leaves the bag and appears once, at A's feet, claimed by A ----
        ActionResult dropped = await ActAsync(server, a, new InventoryDropIntent(band.SlotIndex, id));
        Assert.True(dropped.Accepted, dropped.Reason);
        PlayerInventorySnapshot afterDrop = Assert.IsType<InventoryChangedResult>(dropped.Payload).Inventory;
        Assert.DoesNotContain(afterDrop.AllItems(), item => item.ItemInstanceId == id);
        Assert.Equal(free + 1, afterDrop.FreeCells());
        Assert.Equal(1, runtime.GroundItemCount);
        foreach (WireLoopbackClient client in new[] { a, b })
        {
            await WaitGroundAsync(client, ground => ground.Count == 1, "the dropped item");
            GroundItemSnapshot seen = Assert.Single(Ground(client.Frames).Values);
            Assert.Equal(id, seen.ItemInstanceId);
            Assert.Equal(Band, seen.ItemKind);
            Assert.Equal(targetA.ActorId, seen.ClaimantActorId);
            Assert.True(Vector3.Distance(seen.Position, targetA.Position) < 1f);
            Assert.Equal(1, client.Frames.Select(frame => frame.Decoded.Message).OfType<ServerGroundItems>().SelectMany(m => m.Upserts).Count(u => u.ItemInstanceId == id));
        }

        Flush(server);
        Assert.Equal("Ground", db.Scalar($"SELECT state FROM item_instances WHERE item_instance_id = {id};"));
        Assert.IsType<DBNull>(db.Scalar($"SELECT owner_player_name FROM item_instances WHERE item_instance_id = {id};"));
        Assert.Equal(0, Count(db, $"SELECT COUNT(*) FROM player_inventory_slots WHERE item_instance_id = {id};"));

        // ---- the claim holds the stranger off ----
        ActionResult stranger = await ActAsync(server, b, new GroundPickupIntent(id));
        Assert.False(stranger.Accepted);
        Assert.Contains("reserved for another player", stranger.Reason);
        Assert.Equal(1, runtime.GroundItemCount);

        // ---- the dropper picks it straight back up ----
        ActionResult picked = await ActAsync(server, a, new GroundPickupIntent(id));
        Assert.True(picked.Accepted, picked.Reason);
        Assert.Single(Assert.IsType<InventoryChangedResult>(picked.Payload).Inventory.AllItems(), item => item.ItemInstanceId == id);
        Assert.Equal(0, runtime.GroundItemCount);
        foreach (WireLoopbackClient client in new[] { a, b })
        {
            await WaitGroundAsync(client, ground => ground.Count == 0, "the pickup removed the item");
        }

        Flush(server);
        Assert.Equal("Inventory", db.Scalar($"SELECT state FROM item_instances WHERE item_instance_id = {id};"));
        Assert.Equal(1, Count(db, $"SELECT COUNT(*) FROM player_inventory_slots WHERE item_instance_id = {id};"));

        // ---- a dead player cannot drop ----
        PlayerInventorySnapshot again = runtime.TryCaptureInventorySnapshot(new PlayerSessionId(SessionA))!.Value;
        InventorySlotSnapshot anchor = again.Slots.First(slot => slot.Item?.ItemInstanceId == id);
        WaveIRuntimeKit.Kill(runtime, SessionA);
        ActionResult dead = await ActAsync(server, a, new InventoryDropIntent(anchor.SlotIndex, id));
        Assert.False(dead.Accepted);
        Assert.Contains("dead", dead.Reason);
        Assert.Equal(0, runtime.GroundItemCount);
        Assert.Contains(runtime.TryCaptureInventorySnapshot(new PlayerSessionId(SessionA))!.Value.AllItems(), item => item.ItemInstanceId == id);

        await server.DisconnectAsync(a);
        await server.DisconnectAsync(b);
        await TickAsync(server, 2);
        Flush(server);
    }

    [Fact]
    public async Task APickupThenADropOfTheSameItemInOneTick_StaysVisibleToAClientApplyingDeltasInProductionOrder()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using WireLoopbackClient a = await server.ConnectAndJoinAsync(SessionA);
        await TickAsync(server, 2);
        Place(server, SessionA);
        await TickAsync(server);
        WorldSessionRuntime runtime = server.Runtime;

        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            runtime.InventoryService.TryGrantRolled(KeyOf(SessionA), Band, ItemRarity.Common, new ItemRollSeed(7, 7, 7, 7, 0)));
        await TickAsync(server);
        InventorySlotSnapshot band = runtime.TryCaptureInventorySnapshot(new PlayerSessionId(SessionA))!.Value.Slots.First(slot => slot.Item?.ItemKind == Band);
        long id = band.Item!.Value.ItemInstanceId;

        ActionResult first = await ActAsync(server, a, new InventoryDropIntent(band.SlotIndex, id));
        Assert.True(first.Accepted, first.Reason);
        await WaitGroundAsync(a, ground => ground.ContainsKey(id), "the first drop");
        int messagesBefore = a.Frames.Select(frame => frame.Decoded.Message).OfType<ServerGroundItems>().Count();

        // Pick up and drop again: both actions are queued before the one tick that runs them.
        InventorySlotSnapshot cell = band;
        await a.SendActionAsync(new GroundPickupIntent(id));
        ulong pickupSequence = a.SentActionSequence;
        await a.SendActionAsync(new InventoryDropIntent(cell.SlotIndex, id));
        ulong dropSequence = a.SentActionSequence;
        await TickAsync(server);
        Assert.True((await ResultAsync(a, pickupSequence)).Accepted);
        ActionResult second = await ResultAsync(a, dropSequence);
        Assert.True(second.Accepted, second.Reason);

        Assert.Equal(1, runtime.GroundItemCount);
        await a.WaitForAsync(
            frames => frames.Select(frame => frame.Decoded.Message).OfType<ServerGroundItems>().Count() > messagesBefore,
            "the second ground message");
        List<ServerGroundItems> later = a.Frames.Select(frame => frame.Decoded.Message).OfType<ServerGroundItems>().Skip(messagesBefore).ToList();
        Assert.Contains(later.SelectMany(m => m.Upserts), u => u.ItemInstanceId == id);
        Assert.DoesNotContain(later.SelectMany(m => m.RemovedIds), removed => removed == id);
        Assert.True(Ground(a.Frames).ContainsKey(id));

        await server.DisconnectAsync(a);
        await TickAsync(server, 2);
        Flush(server);
    }

    [Fact]
    public async Task OverflowDrops_ClaimBlocksAStranger_ClaimantPicksUp_OneWinnerPerRace_Despawns_AndARestartRestoresWithoutDuplicates()
    {
        using MigrationTestDatabase db = new();
        long idForClaimantPickup;
        long idForRace;
        long idLeftOnGround;

        await using (WireLoopbackServer server = await StartAsync(db))
        {
            await using WireLoopbackClient a = await server.ConnectAndJoinAsync(SessionA);
            await using WireLoopbackClient b = await server.ConnectAndJoinAsync(SessionB);
            await TickAsync(server, 2);
            IAuthoritativePlayerTarget targetA = Place(server, SessionA);
            Place(server, SessionB);
            await TickAsync(server);
            WorldSessionRuntime runtime = server.Runtime;
            Assert.Equal(0, runtime.GroundItemCount);

            // ---- overflow: A's bag is full, so the three bands land on the ground, claimed by A ----
            runtime.InventoryService.FillBagLeaving(KeyOf(SessionA), freeCells: 0);
            string bagBefore = runtime.TryCaptureInventorySnapshot(new PlayerSessionId(SessionA))!.Value.Layout();
            ulong dropTick = runtime.CurrentServerTick;
            runtime.GrantLoot(new PlayerSessionId(SessionA), "Wolf", enemyActorId: 5000, dropTick, targetA.Position);
            Assert.Equal(3, runtime.GroundItemCount);
            Assert.Equal(bagBefore, runtime.TryCaptureInventorySnapshot(new PlayerSessionId(SessionA))!.Value.Layout());
            await TickAsync(server);

            // Both clients get the three items with A's claim; the loot push still says "lost".
            foreach (WireLoopbackClient client in new[] { a, b })
            {
                await WaitGroundAsync(client, ground => ground.Count == 3, "three ground items");
                Dictionary<long, GroundItemSnapshot> seen = Ground(client.Frames);
                Assert.All(seen.Values, item =>
                {
                    Assert.Equal(Band, item.ItemKind);
                    Assert.Equal(targetA.ActorId, item.ClaimantActorId);
                    Assert.True(item.ClaimExpiresTick >= dropTick + (ulong)PlayerInventoryService.GroundClaimTicks);
                    Assert.True(Vector3.Distance(item.Position, targetA.Position) < 1f);
                });
            }

            Assert.All(
                a.Frames.Select(frame => frame.Decoded.Message).OfType<InventoryPushMessage>().SelectMany(push => push.Notices),
                notice => Assert.True(notice.Lost));
            long[] ids = [.. Ground(a.Frames).Keys.Order()];
            idForClaimantPickup = ids[0];
            idForRace = ids[1];
            idLeftOnGround = ids[2];
            Flush(server);
            Assert.Equal(3, Count(db, "SELECT COUNT(*) FROM item_instances WHERE state = 'Ground' AND owner_player_name IS NULL;"));
            Assert.Equal(0, Count(db, $"SELECT COUNT(*) FROM player_inventory_slots WHERE item_instance_id IN ({idForClaimantPickup},{idForRace},{idLeftOnGround});"));

            // ---- the claim blocks a stranger, who has room ----
            int bFree = runtime.TryCaptureInventorySnapshot(new PlayerSessionId(SessionB))!.Value.FreeCells();
            ActionResult stranger = await ActAsync(server, b, new GroundPickupIntent(idForClaimantPickup));
            Assert.False(stranger.Accepted);
            Assert.Contains("reserved for another player", stranger.Reason);
            Assert.Equal(bFree, runtime.TryCaptureInventorySnapshot(new PlayerSessionId(SessionB))!.Value.FreeCells());
            Assert.Equal(3, runtime.GroundItemCount);

            // ---- the claimant with a full bag is told so, then picks up once there is room ----
            ActionResult full = await ActAsync(server, a, new GroundPickupIntent(idForClaimantPickup));
            Assert.False(full.Accepted);
            Assert.Contains("backpack is full", full.Reason);
            Assert.Equal(3, runtime.GroundItemCount);

            await DestroyOneBandAsync(server, a, SessionA);
            ActionResult picked = await ActAsync(server, a, new GroundPickupIntent(idForClaimantPickup));
            Assert.True(picked.Accepted, picked.Reason);
            PlayerInventorySnapshot afterPickup = Assert.IsType<InventoryChangedResult>(picked.Payload).Inventory;
            Assert.Single(afterPickup.AllItems(), item => item.ItemInstanceId == idForClaimantPickup);
            Assert.Equal(2, runtime.GroundItemCount);
            foreach (WireLoopbackClient client in new[] { a, b })
            {
                await WaitGroundAsync(client, ground => ground.Count == 2 && !ground.ContainsKey(idForClaimantPickup), "the pickup removed from both lists");
            }

            Flush(server);
            Assert.Equal("Inventory", db.Scalar($"SELECT state FROM item_instances WHERE item_instance_id = {idForClaimantPickup};"));
            Assert.Equal(KeyOf(SessionA), db.Scalar($"SELECT owner_player_name FROM item_instances WHERE item_instance_id = {idForClaimantPickup};"));
            Assert.IsType<DBNull>(db.Scalar($"SELECT world_position_x FROM item_instances WHERE item_instance_id = {idForClaimantPickup};"));
            Assert.Equal(1, Count(db, $"SELECT COUNT(*) FROM player_inventory_slots WHERE item_instance_id = {idForClaimantPickup};"));

            // ---- the claim lapses: the clients see it free; two pickups in one tick, one winner ----
            runtime.StepGroundItems(dropTick + (ulong)PlayerInventoryService.GroundClaimTicks);
            await TickAsync(server);
            foreach (WireLoopbackClient client in new[] { a, b })
            {
                await WaitGroundAsync(client, ground => ground.Count == 2 && ground.Values.All(item => item.ClaimantActorId == 0 && item.ClaimExpiresTick == 0), "the claims lapsed");
            }

            await DestroyOneBandAsync(server, a, SessionA);
            await a.SendActionAsync(new GroundPickupIntent(idForRace));
            await b.SendActionAsync(new GroundPickupIntent(idForRace));
            ulong sequenceA = a.SentActionSequence;
            ulong sequenceB = b.SentActionSequence;
            await TickAsync(server);
            ActionResult raceA = await ResultAsync(a, sequenceA);
            ActionResult raceB = await ResultAsync(b, sequenceB);
            Assert.True(raceA.Accepted ^ raceB.Accepted, $"A: {raceA.Accepted} {raceA.Reason}; B: {raceB.Accepted} {raceB.Reason}");
            ActionResult loser = raceA.Accepted ? raceB : raceA;
            Assert.Contains("item is gone", loser.Reason);
            string winnerKey = raceA.Accepted ? KeyOf(SessionA) : KeyOf(SessionB);
            string loserSession = raceA.Accepted ? SessionB : SessionA;
            Assert.Single(runtime.TryCaptureInventorySnapshot(new PlayerSessionId(raceA.Accepted ? SessionA : SessionB))!.Value.AllItems(), item => item.ItemInstanceId == idForRace);
            Assert.DoesNotContain(runtime.TryCaptureInventorySnapshot(new PlayerSessionId(loserSession))!.Value.AllItems(), item => item.ItemInstanceId == idForRace);
            Assert.Equal(1, runtime.GroundItemCount);
            await TickAsync(server);
            Flush(server);
            Assert.Equal(1, Count(db, $"SELECT COUNT(*) FROM player_inventory_slots WHERE item_instance_id = {idForRace};"));
            Assert.Equal(winnerKey, db.Scalar($"SELECT player_name FROM player_inventory_slots WHERE item_instance_id = {idForRace};"));
            Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM (SELECT 1 FROM player_inventory_slots GROUP BY world_id, item_instance_id HAVING COUNT(*) > 1);"));
            foreach (WireLoopbackClient client in new[] { a, b })
            {
                await WaitGroundAsync(client, ground => ground.Count == 1 && ground.ContainsKey(idLeftOnGround), "the race winner removed from both lists");
            }

            await server.DisconnectAsync(a);
            await server.DisconnectAsync(b);
            await TickAsync(server, 2);
            Flush(server);
        }

        SqlitePools.Release(db.DatabasePath);

        // ---- restart: the one item left is back on the ground, free, once, and its id stays reserved ----
        Assert.Equal(1, Count(db, "SELECT COUNT(*) FROM item_instances WHERE state = 'Ground';"));
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            WorldSessionRuntime runtime = server.Runtime;
            Assert.Equal(1, runtime.GroundItemCount);
            Assert.True(runtime.NextItemInstanceId > Math.Max(idLeftOnGround, Math.Max(idForClaimantPickup, idForRace)));
            Assert.Equal(1, Count(db, "SELECT COUNT(*) FROM item_instances WHERE state = 'Ground';"));
            Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM item_instances AS i WHERE i.state = 'Ground' AND EXISTS (SELECT 1 FROM player_inventory_slots AS s WHERE s.item_instance_id = i.item_instance_id);"));

            await using WireLoopbackClient a = await server.ConnectAndJoinAsync(SessionA);
            await TickAsync(server);
            // A joiner gets the Reset with the item, with no claim.
            await WaitGroundAsync(a, ground => ground.Count == 1, "the restored item");
            GroundItemSnapshot restored = Assert.Single(Ground(a.Frames).Values);
            Assert.Equal(idLeftOnGround, restored.ItemInstanceId);
            Assert.Equal(0, restored.ClaimantActorId);
            Assert.Equal(0UL, restored.ClaimExpiresTick);
            Assert.True(a.Frames.Select(frame => frame.Decoded.Message).OfType<ServerGroundItems>().First().Reset);

            // ---- despawn: five minutes on, the item is gone from memory, the clients and the database ----
            runtime.StepGroundItems(runtime.CurrentServerTick + (ulong)PlayerInventoryService.GroundDespawnTicks);
            Assert.Equal(0, runtime.GroundItemCount);
            Assert.Equal(InventoryEquipOutcome.ItemGone, runtime.InventoryService.TryPickUpGround(KeyOf(SessionA), idLeftOnGround, Spot, runtime.CurrentServerTick));
            await TickAsync(server);
            await WaitGroundAsync(a, ground => ground.Count == 0, "the despawn");
            Flush(server);
            Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM item_instances WHERE state = 'Ground';"));
            Assert.Equal(0, Count(db, $"SELECT COUNT(*) FROM item_instances WHERE item_instance_id = {idLeftOnGround};"));
            await server.DisconnectAsync(a);
        }

        SqlitePools.Release(db.DatabasePath);
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            Assert.Equal(0, server.Runtime.GroundItemCount);
        }
    }
}
