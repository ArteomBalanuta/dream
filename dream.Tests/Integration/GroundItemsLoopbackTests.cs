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

    // The client's ground list as ClientGroundItemCache will keep it: replayed from every ground message
    // received, in order (Reset replaces, upserts and removals are idempotent).
    private static Dictionary<long, GroundItemSnapshot> Ground(IReadOnlyList<WireFrame> frames)
    {
        Dictionary<long, GroundItemSnapshot> list = [];
        foreach (ServerGroundItems message in frames.Select(frame => frame.Decoded.Message).OfType<ServerGroundItems>())
        {
            Assert.InRange(message.Upserts.Count + message.RemovedIds.Count, 1 - 1, ServerGroundItems.MaxEntries);
            if (message.Reset)
            {
                list.Clear();
            }

            foreach (long id in message.RemovedIds)
            {
                list.Remove(id);
            }

            foreach (GroundItemSnapshot item in message.Upserts)
            {
                list[item.ItemInstanceId] = item;
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

        SqliteConnection.ClearAllPools();

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

        SqliteConnection.ClearAllPools();
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            Assert.Equal(0, server.Runtime.GroundItemCount);
        }
    }
}
