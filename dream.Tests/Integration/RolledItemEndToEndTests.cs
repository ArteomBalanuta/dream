using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Tests.Support;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Server.Items;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Integration;

// Wave H glue: all three streams over real TCP on a real database. The admin command rolls the gear,
// a client equips all four slots with intents, and the client's effective stats, computed from the
// snapshot alone as PlayScene does, equal the server's. Then the gear has to survive a restart, a
// --worlds copy and a full export, each followed by a join.
[Collection(TimingSensitiveCollection.Name)]
public sealed class RolledItemEndToEndTests(ITestOutputHelper output)
{
    private const string Session = "rolled-e2e";
    private const string Character = $"Name {Session}";

    private static readonly string[] Gear = ["gear.iron-blade", "gear.ward-lantern", "gear.leather-cap", "gear.leather-vest"];

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db, string? world = null)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(world)));
        return await WireLoopbackServer.StartAsync(host);
    }

    private static (int Exit, string Out, string Err) Items(MigrationTestDatabase db, params string[] args)
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        int exit = ItemCli.Run(args, () => db.Schema, stdout, stderr, id => WorldHostLease.TryAcquire(db.DatabasePath, id));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static void Worlds(MigrationTestDatabase db, params string[] args)
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        int exit = WorldCli.Run(args, () => new SqliteWorldCatalog(db.Schema), stdout, stderr, id => WorldHostLease.TryAcquire(db.DatabasePath, id));
        Assert.True(exit == 0, $"--worlds {string.Join(' ', args)} exited {exit}: {stderr}");
    }

    private static PlayerInventorySnapshot JoinInventory(WireLoopbackClient client) =>
        client.Frames.Select(frame => frame.Decoded.Message).OfType<JoinAcceptedMessage>().Last().Inventory!.Value;

    // Sends the action and ticks until its result is back; returns the accepted inventory.
    private static async Task<PlayerInventorySnapshot> ActAsync(WireLoopbackServer server, WireLoopbackClient client, IPlayerActionPayload payload)
    {
        await client.SendActionAsync(payload);
        ulong sequence = client.SentActionSequence;
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (true)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
            ServerActionMessage? result = client.Frames
                .Select(frame => frame.Decoded.Message)
                .OfType<ServerActionMessage>()
                .FirstOrDefault(message => message.Result.ActionSequence == sequence);
            if (result != null)
            {
                Assert.True(result.Result.Accepted, result.Result.Rejection?.ToString());
                return Assert.IsType<InventoryChangedResult>(result.Result.Payload).Inventory;
            }

            Assert.True(DateTime.UtcNow < deadline, $"action {sequence} was never answered");
            await Task.Delay(2);
        }
    }

    // The client side is PlayScene.ApplyInventorySnapshot's read of the snapshot; the server side is the
    // live combatant, and the actor on the wire carries the base stats and the max health.
    private static void AssertClientStatsEqualTheServers(WireLoopbackServer server, PlayerInventorySnapshot inventory)
    {
        EquipmentLoadout loadout = new();
        PlayScene.ReadEquipment(inventory, loadout);
        PlayerCombatant client = new(() => Vector3.Zero);
        client.ApplyEquipment(loadout);

        PlayerCombatant authoritative = server.Runtime.Player.Combatant;
        Assert.Equal(authoritative.Stats.Strength, client.Stats.Strength);
        Assert.Equal(
            BitConverter.SingleToInt32Bits(authoritative.Stats.AttackSpeed),
            BitConverter.SingleToInt32Bits(client.Stats.AttackSpeed));
        Assert.Equal(authoritative.Stats.Vitality, client.Stats.Vitality);
        Assert.Equal(authoritative.EffectiveArmor, client.EffectiveArmor);
        Assert.Equal(authoritative.DerivedStats.MaxHealth, client.DerivedStats.MaxHealth);

        ActorSnapshot actor = server.Runtime.CaptureActorSnapshots().Single(a => a.SessionId == Session);
        Assert.Equal(authoritative.BaseStats.Strength, actor.CombatStats!.Value.Strength);
        Assert.Equal(authoritative.BaseStats.Vitality, actor.CombatStats!.Value.Vitality);
        Assert.Equal(authoritative.DerivedStats.MaxHealth, actor.MaxHealth);
    }

    private static string[] Worn(PlayerInventorySnapshot inventory)
    {
        List<InventorySlotSnapshot> rows = [inventory.EquippedSlot!.Value, .. inventory.Equipment ?? []];
        return [.. rows.Select(row =>
            $"{row.SlotIndex}={row.Item?.ItemKind}#{row.Item?.ItemInstanceId}:{row.Item?.Rarity}:" +
            string.Join(',', (row.Item?.Modifiers ?? []).Select(m => $"{m.Stat}/{m.Op}/{BitConverter.SingleToInt32Bits(m.Value)}")))];
    }

    private static string[] StoredRolls(MigrationTestDatabase db, long worldId)
    {
        SqliteConnection.ClearAllPools();
        using SqliteConnection connection = db.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT item_instance_id || '=' || rolled_modifiers FROM item_instances WHERE world_id = $w AND rolled_modifiers IS NOT NULL ORDER BY item_instance_id;";
        command.Parameters.AddWithValue("$w", worldId);
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
    }

    private static long WorldIdOf(MigrationTestDatabase db, string name)
    {
        SqliteConnection.ClearAllPools();
        return Convert.ToInt64(db.Scalar($"SELECT world_id FROM worlds WHERE name = '{name}';"), System.Globalization.CultureInfo.InvariantCulture);
    }

    // A join on the given world: the same gear, the same rolls and matching stats, and no starter top-up.
    private async Task JoinAndCheckAsync(MigrationTestDatabase db, string? world, string[] worn, int itemCount)
    {
        await using WireLoopbackServer server = await StartAsync(db, world);
        WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
        PlayerInventorySnapshot inventory = JoinInventory(client);
        Assert.Equal(worn, Worn(inventory));
        Assert.Equal(itemCount, inventory.Slots.Count(slot => slot.Item != null));
        AssertClientStatsEqualTheServers(server, inventory);
        await server.DisconnectAsync(client);
        output.WriteLine($"{world ?? "default"}: {string.Join(" | ", worn)}");
    }

    [Fact]
    public async Task GrantedGear_EquippedInFourSlots_SurvivesRestartCopyAndExport_WithClientAndServerStatsEqual()
    {
        using MigrationTestDatabase db = new();

        // The first join records the starter grant --items needs.
        await using (WireLoopbackServer first = await StartAsync(db))
        {
            WireLoopbackClient client = await first.ConnectAndJoinAsync(Session);
            await first.DisconnectAsync(client);
            Assert.Equal(ItemCli.ExitWorldHeld, Items(db, "grant", "1", Character, Gear[0]).Exit);
        }

        List<int> slots = [];
        foreach (string kind in Gear)
        {
            (int exit, string stdout, string stderr) = Items(db, "grant", "1", Character, kind, "--rarity", "rare");
            Assert.True(exit == 0, $"grant {kind} exited {exit}: {stderr}");
            slots.Add(int.Parse(stdout.Split('\t')[1], System.Globalization.CultureInfo.InvariantCulture));
        }

        string[] rolls = StoredRolls(db, 1);
        Assert.Equal(Gear.Length, rolls.Length);

        string[] worn;
        int itemCount;
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            PlayerInventorySnapshot inventory = JoinInventory(client);
            foreach (int slot in slots)
            {
                InventoryItemSnapshot item = inventory.ItemAt(slot)!.Value;
                Assert.True(item.IsRolled);
                Assert.Equal(ItemRarity.Rare, item.Rarity);
                Assert.NotEmpty(item.Modifiers!);
                inventory = await ActAsync(server, client, new InventoryEquipIntent(slot));
            }

            worn = Worn(inventory);
            Assert.Equal(4, worn.Length);
            Assert.All(worn, row => Assert.Contains("Rare", row, StringComparison.Ordinal));
            // The four items left the bag: none of the worn ids is still on the board.
            long[] wornIds = new[] { inventory.EquippedSlot!.Value }.Concat(inventory.Equipment!).Select(row => row.Item!.Value.ItemInstanceId).ToArray();
            Assert.All(inventory.Slots, row => Assert.DoesNotContain(row.Item!.Value.ItemInstanceId, wornIds));
            AssertClientStatsEqualTheServers(server, inventory);

            // Every modifier of all four slots is in the effective numbers: Armor comes from gear alone.
            float expectedArmor = new[] { inventory.EquippedSlot!.Value }.Concat(inventory.Equipment!)
                .SelectMany(row => row.Item!.Value.Modifiers!)
                .Where(modifier => modifier.Stat == StatId.Armor)
                .Sum(modifier => modifier.Value);
            Assert.True(expectedArmor > 0f);
            Assert.Equal((int)expectedArmor, server.Runtime.Player.Combatant.EffectiveArmor);
            Assert.Equal(HeldLightKind.Lantern, server.Runtime.CaptureActorSnapshots().Single(actor => actor.SessionId == Session).HeldLight);

            itemCount = inventory.Slots.Count(slot => slot.Item != null);
            await server.DisconnectAsync(client);
        }

        Assert.Equal(rolls, StoredRolls(db, 1));

        // Restart, then a copy, then an export of the copy: each one joined by the same client.
        await JoinAndCheckAsync(db, world: null, worn, itemCount);

        Worlds(db, "copy", "1", "rolled-copy");
        long copy = WorldIdOf(db, "rolled-copy");
        Assert.Equal(rolls, StoredRolls(db, copy));
        await JoinAndCheckAsync(db, "rolled-copy", worn, itemCount);

        Worlds(db, "export", "rolled-copy", "rolled-export");
        long export = WorldIdOf(db, "rolled-export");
        Assert.Equal(rolls, StoredRolls(db, export));
        await JoinAndCheckAsync(db, "rolled-export", worn, itemCount);

        Assert.Equal(rolls, StoredRolls(db, 1));
        Assert.Equal(rolls, StoredRolls(db, copy));
        Assert.Equal(rolls, StoredRolls(db, export));
    }
}
