using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Inventory;
using Dream.Simulation.Runtime;
using Dream.Tests.Load;
using Dream.Tests.Server.Persistence;

namespace Dream.Tests.Integration;

// Wave 12 tester, SCALE-7b per-player inventory loads, over loopback against the production host
// shape (SQLite persistence, deferred joins with the join prefetch) and the real
// RemoteClientSession:
// - an equip sent as early as a client can send it (before its join is applied, then again in
//   the first tick after) never sees a half-loaded inventory: it is dropped before the join, and
//   after the join it is accepted against the persisted inventory, never a re-seeded one;
// - after a restart nothing is loaded, a newcomer's starter items take ids above every persisted
//   id, and the returning player's inventory comes back intact;
// - 2000 distinct players churning through the server keep the loaded inventory, profile and
//   name maps bounded by the departed-profile LRU, and managed memory flat (LoadFact).
public sealed class Wave12InventoryLoopbackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Restart_EarlyEquip_SeesThePersistedInventory_AndNewItemIdsStayAboveTheDatabaseMax()
    {
        using PersistenceTestDatabase database = new();
        PlayerInventorySnapshot firstVisit;
        long equippedFirst;
        // Run 1: Alpha joins, equips slot 2 and leaves; the server shuts down.
        {
            await using Wave12DeltaActorLoopbackTests.Rig rig = await StartAsync(database);
            Wave12DeltaActorLoopbackTests.Peer alpha = await rig.JoinAsync("w12-alpha");
            List<AuthoritativeActionResult> results = Collect(alpha);
            await EquipAsync(rig, alpha, sequence: 1, slotIndex: 2);
            await StepUntilAsync(rig, alpha, () => results.Count >= 1);
            Assert.True(results[0].Accepted, results[0].Rejection?.ToString());
            firstVisit = InventoryOf(results[0]);
            equippedFirst = firstVisit.EquippedSlot!.Value.Item!.Value.ItemInstanceId;
        }

        long maxAfterRun1 = database.Count("SELECT COALESCE(MAX(item_instance_id), 0) FROM item_instances;");
        long itemRows = database.Count("SELECT COUNT(*) FROM item_instances;");
        output.WriteLine($"run 1: {itemRows} item rows, max id {maxAfterRun1}, Alpha equipped {equippedFirst}");

        // Run 2: nothing is loaded at startup. Beta joins first and gets a starter inventory; then
        // Alpha connects and asks to equip before its join is even applied, and again right after.
        {
            await using Wave12DeltaActorLoopbackTests.Rig rig = await StartAsync(database);
            WorldSessionRuntime runtime = rig.Runtime;
            Assert.Equal(0, runtime.LoadedInventoryCount);
            Assert.True(runtime.NextItemInstanceId > maxAfterRun1, $"next item id {runtime.NextItemInstanceId} <= persisted max {maxAfterRun1}");

            Wave12DeltaActorLoopbackTests.Peer beta = await rig.JoinAsync("w12-beta");
            PlayerInventorySnapshot betaInventory = runtime.TryCaptureInventorySnapshot(new PlayerSessionId("w12-beta"))!.Value;
            long[] betaIds = ItemIds(betaInventory);
            Assert.NotEmpty(betaIds);
            Assert.All(betaIds, id => Assert.True(id > maxAfterRun1, $"Beta's starter item {id} reuses a persisted id (max {maxAfterRun1})"));

            Wave12DeltaActorLoopbackTests.Peer alpha = await rig.ConnectWithoutJoiningAsync("w12-alpha");
            List<AuthoritativeActionResult> results = Collect(alpha);
            // Before the join is applied: the server has no joined context for it yet.
            alpha.Session.QueuePlayerAction(new PlayerActionMessage(new PlayerSessionId("w12-alpha"), 1, new InventoryEquipIntent(3)));
            Assert.True(await alpha.Session.FlushOutboundAsync(TimeSpan.FromSeconds(10)));
            await rig.WaitUntilJoinedAsync(alpha);
            // The first tick after the join.
            await EquipAsync(rig, alpha, sequence: 2, slotIndex: 2);
            await StepUntilAsync(rig, alpha, () => results.Any(result => result.ActionSequence == 2));
            AuthoritativeActionResult early = results.Single(result => result.ActionSequence == 2);
            Assert.True(early.Accepted, early.Rejection?.ToString());
            // The pre-join equip was answered with a rejection (join required), not applied to an
            // inventory that was not loaded yet.
            Assert.False(results.Single(result => result.ActionSequence == 1).Accepted);
            PlayerInventorySnapshot secondVisit = InventoryOf(early);
            Assert.Equal(ItemIds(firstVisit), ItemIds(secondVisit));
            Assert.Equal(equippedFirst, secondVisit.EquippedSlot!.Value.Item!.Value.ItemInstanceId);
            output.WriteLine($"run 2: pre-join equip results {results.Count(result => result.ActionSequence == 1)}; Beta ids {string.Join(",", betaIds)}; Alpha ids {string.Join(",", ItemIds(secondVisit))}");
            Assert.Equal(2, runtime.LoadedInventoryCount);
        }

        // No starter inventory was seeded twice: Alpha's and Beta's only.
        Assert.Equal(2 * itemRows, database.Count("SELECT COUNT(*) FROM item_instances;"));
        Assert.Equal(database.Count("SELECT COUNT(*) FROM item_instances;"), database.Count("SELECT COUNT(DISTINCT item_instance_id) FROM item_instances;"));
    }

    // 2000 distinct players join and leave one after another over loopback. The departed-profile
    // LRU (1024 by default) bounds the loaded inventories, item instances, cached profiles and
    // profile names; managed memory after a full GC must not keep growing past the LRU's fill.
    [LoadFact]
    public async Task TwoThousandPlayerChurn_OverLoopback_KeepsInventoryStateAndMemoryBounded()
    {
        const int Players = 2000;
        using PersistenceTestDatabase database = new();
        await using Wave12DeltaActorLoopbackTests.Rig rig = await StartAsync(database);
        WorldSessionRuntime runtime = rig.Runtime;
        Wave12DeltaActorLoopbackTests.Peer anchor = await rig.JoinAsync("w12-anchor");
        Stopwatch clock = Stopwatch.StartNew();
        List<(int Players, long Bytes, int Inventories, int Items, int Profiles, int Names, int Registry)> samples = [];
        for (int i = 1; i <= Players; i++)
        {
            Wave12DeltaActorLoopbackTests.Peer visitor = await rig.JoinAsync($"w12-churn-{i:D4}");
            await rig.DisconnectAsync(visitor);
            if (i % 250 == 0)
            {
                // Keep the anchor's chain and silence watchdog fed.
                await rig.StepAsync();
                await rig.VerifyAllAsync();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                samples.Add((i, GC.GetTotalMemory(forceFullCollection: true), runtime.LoadedInventoryCount, runtime.LoadedItemInstanceCount,
                    runtime.ProfileCacheCount, runtime.KnownProfileNameCount, runtime.Sessions.Count));
                output.WriteLine($"{i} players ({clock.Elapsed.TotalSeconds:0} s): managed {samples[^1].Bytes / 1048576d:0.0} MB, inventories {samples[^1].Inventories}, items {samples[^1].Items}, profiles {samples[^1].Profiles}, names {samples[^1].Names}, registry {samples[^1].Registry}");
            }
        }

        // Which collections hold more than the LRU (a slow leak shows here).
        foreach (string line in LargeCollections(rig.Host, "host", 0).Concat(LargeCollections(runtime, "runtime", 0)).Concat(LargeCollections(rig.Service, "service", 0)))
        {
            output.WriteLine(line);
        }

        int lru = runtime.MaxDepartedProfiles;
        var last = samples[^1];
        // The anchor and the local seat are active; everyone else departed.
        Assert.True(last.Inventories <= lru + 2, $"{last.Inventories} inventories loaded with an LRU of {lru}");
        Assert.True(last.Profiles <= 2 * (lru + 2), $"{last.Profiles} cached profiles");
        Assert.True(last.Names <= lru + 2, $"{last.Names} known profile names");
        // Once the LRU is full (past ~1024 players) memory stays flat: compare 1250 with 2000.
        var full = samples.First(sample => sample.Players >= 1250);
        double growthMb = (last.Bytes - full.Bytes) / 1048576d;
        output.WriteLine($"managed growth from {full.Players} to {last.Players} players: {growthMb:0.00} MB");
        Assert.True(growthMb < 8d, $"managed memory grew {growthMb:0.0} MB between {full.Players} and {last.Players} players");
    }

    // Collections reachable from obj's fields (two levels deep) with more than 1100 entries.
    private static IEnumerable<string> LargeCollections(object? obj, string path, int depth)
    {
        if (obj == null || depth > 2)
        {
            yield break;
        }

        for (Type? type = obj.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (System.Reflection.FieldInfo field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (field.FieldType.IsPrimitive || field.FieldType == typeof(string) || field.FieldType.IsPointer)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = field.GetValue(obj);
                }
                catch (Exception)
                {
                    continue;
                }

                if (value is System.Collections.ICollection collection)
                {
                    if (collection.Count > 1100)
                    {
                        yield return $"{path}.{field.Name}: {collection.Count}";
                    }
                }
                else if (value != null && !field.FieldType.IsValueType && field.FieldType.Namespace?.StartsWith("Dream", StringComparison.Ordinal) == true)
                {
                    foreach (string line in LargeCollections(value, $"{path}.{field.Name}", depth + 1))
                    {
                        yield return line;
                    }
                }
            }
        }
    }

    private static Task<Wave12DeltaActorLoopbackTests.Rig> StartAsync(PersistenceTestDatabase database)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, database.SchemaPath));
        return Wave12DeltaActorLoopbackTests.Rig.StartAsync(new NullOutput(), recordExpected: false, host: host);
    }

    private static List<AuthoritativeActionResult> Collect(Wave12DeltaActorLoopbackTests.Peer peer)
    {
        List<AuthoritativeActionResult> results = [];
        peer.Observer = message =>
        {
            if (message is ServerActionMessage action)
            {
                results.Add(action.Result);
            }
        };
        return results;
    }

    private static async Task EquipAsync(Wave12DeltaActorLoopbackTests.Rig rig, Wave12DeltaActorLoopbackTests.Peer peer, ulong sequence, int slotIndex)
    {
        peer.Session.QueuePlayerAction(new PlayerActionMessage(new PlayerSessionId(peer.SessionId), sequence, new InventoryEquipIntent(slotIndex)));
        await rig.WaitUntilAsync(
            () => rig.Host.TryGetConnectionContext(peer.Connection.ConnectionId, out ServerRoutingContext? context) && context?.LastActionMessage?.ActionSequence >= sequence,
            $"the server to read {peer.SessionId}'s action {sequence}");
    }

    private static async Task StepUntilAsync(Wave12DeltaActorLoopbackTests.Rig rig, Wave12DeltaActorLoopbackTests.Peer peer, Func<bool> condition)
    {
        for (int i = 0; i < 64 && !condition(); i++)
        {
            await rig.StepAsync();
            await rig.DrainAllAsync();
        }

        Assert.True(condition(), $"{peer.SessionId}'s action result never arrived");
    }

    private static PlayerInventorySnapshot InventoryOf(AuthoritativeActionResult result)
    {
        return Assert.IsType<InventoryChangedResult>(result.Payload).Inventory;
    }

    private static long[] ItemIds(PlayerInventorySnapshot inventory)
    {
        return inventory.Slots.Where(slot => slot.Item.HasValue).Select(slot => slot.Item!.Value.ItemInstanceId).Distinct().Order().ToArray();
    }

    private sealed class NullOutput : ITestOutputHelper
    {
        public void WriteLine(string message)
        {
        }

        public void WriteLine(string format, params object[] args)
        {
        }
    }
}
