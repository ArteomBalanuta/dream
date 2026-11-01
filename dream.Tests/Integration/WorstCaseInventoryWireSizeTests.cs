using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;
using Dream.Simulation.Runtime;
using Dream.Tests.Multiplayer.Protocol;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;
using Dream.Tests.Support;

namespace Dream.Tests.Integration;

// Wave I glue (I-D26), redone for classic HUD iteration 2: the largest inventory a player can reach is a
// full 8x8 bag of 64 rolled rare 1x1 items and all twelve slots worn, and every item travels once (a worn
// item is not in the bag). The real join frame and the real push of the last item stay within the gate,
// and so does that push with the most notices one can carry. If this fails, trim as I-D26 says; do not
// raise the gate without recording why.
[Collection(TimingSensitiveCollection.Name)]
public sealed class WorstCaseInventoryWireSizeTests(ITestOutputHelper output)
{
    private const string Session = "worst-case";
    private const int GateBytes = 16_000;

    // One rolled rare item for each of the twelve slots; the two bands take the two ring slots.
    private static readonly string[] Worn =
    [
        "gear.iron-blade", "gear.ward-lantern", "gear.leather-cap", "gear.leather-vest", "gear.iron-gauntlets",
        "gear.iron-boots", "gear.plated-leggings", "gear.ward-pendant", "gear.iron-band", "gear.iron-band",
        "gear.travel-cloak", "gear.ember-wisp",
    ];

    // The 1x1 kinds that fill the bag, alternating.
    private static readonly string[] Filler = ["gear.ward-pendant", "gear.iron-band"];

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath));
        return await WireLoopbackServer.StartAsync(host);
    }

    [Fact]
    public async Task SixtyFourRolledWithTwelveWorn_FitTheGate_InTheJoinAndInThePush()
    {
        using MigrationTestDatabase db = new();
        string key = WorldSessionRuntime.ResolveJoinPlayerName(Session, $"Name {Session}");
        int pushBytes;
        InventoryPushMessage push;
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            // The last bag item comes from a kill: a table of one kind, always rare.
            server.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1, common: 0, uncommon: 0, rare: 1, kinds: [Filler[0]]);
            WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            PlayerInventoryService inventory = server.Runtime.InventoryService;

            // Only rolled items: the starter kit goes first.
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryUnequip(key, inventory.BuildSnapshot(key).EquippedSlot!.Value.SlotIndex));
            foreach (InventorySlotSnapshot slot in inventory.BuildSnapshot(key).Slots.Where(slot => slot.Item != null))
            {
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryDestroy(key, slot.SlotIndex, slot.Item!.Value.ItemInstanceId));
            }

            // Twelve worn, one per slot (a ring goes to the first empty ring slot).
            for (int i = 0; i < Worn.Length; i++)
            {
                Assert.Equal(
                    InventoryEquipOutcome.Equipped,
                    inventory.TryGrantRolled(key, Worn[i], ItemRarity.Rare, new ItemRollSeed(7, 7, 7, 7, i)));
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(key, inventory.AnchorOf(key, Worn[i])));
            }

            Assert.Empty(inventory.BuildSnapshot(key).Slots);

            // 63 rolled 1x1 items in the bag; the 64th comes from the kill.
            for (int i = 0; i < BagGrid.CellCount - 1; i++)
            {
                Assert.Equal(
                    InventoryEquipOutcome.Equipped,
                    inventory.TryGrantRolled(key, Filler[i % Filler.Length], ItemRarity.Rare, new ItemRollSeed(8, 8, 8, 8, i)));
            }

            // One command with the facing, so the wolf is aimed at along it.
            await client.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
            await server.TickAsync();
            for (int attempt = 0; attempt < 128 && !Pushes(client).Any(); attempt++)
            {
                if (server.Runtime.Enemies.ActiveActors.Count > 0)
                {
                    WaveIRuntimeKit.AimWolfAt(server.Runtime, Session, wolfHealth: 1);
                }

                await client.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, Vector3.UnitZ, attack: true);
                await server.TickAsync();
                Assert.True(await server.FlushAllAsync());
                await client.WaitForSnapshotTickAsync(server.Runtime.CurrentServerTick);
            }

            WireFrame frame = client.Frames.First(f => f.Decoded.Message is InventoryPushMessage);
            push = (InventoryPushMessage)frame.Decoded.Message;
            pushBytes = frame.Payload.Length;
            await server.DisconnectAsync(client);
        }

        PlayerInventorySnapshot full = push.Inventory!.Value;
        Assert.Equal(64, full.Slots.Count(slot => slot.Item is { IsRolled: true }));
        Assert.Equal(0, full.FreeCells());
        Assert.Equal(11, full.Equipment!.Count(row => row.Item != null));
        Assert.NotNull(full.EquippedSlot!.Value.Item);

        // The most one push can say, around the same inventory.
        InventoryPushMessage loudest = new(
            full,
            [.. Enumerable.Repeat(new LootNotice("Leather Vest", ItemRarity.Uncommon, Lost: false), InventoryPushMessage.MaxNotices)]);
        int loudestBytes = new TransportJsonCodec().Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, InventoryPushMessage.Kind, ulong.MaxValue),
            loudest).Length;

        int joinBytes;
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            WireFrame join = client.Frames.First(f => f.Decoded.Message is JoinAcceptedMessage);
            joinBytes = join.Payload.Length;
            PlayerInventorySnapshot joined = ((JoinAcceptedMessage)join.Decoded.Message).Inventory!.Value;
            Assert.Equal(64, joined.Slots.Count(slot => slot.Item is { IsRolled: true }));
            Assert.Equal(11, joined.Equipment!.Count(row => row.Item != null));
            Assert.NotNull(joined.EquippedSlot!.Value.Item);
            await server.DisconnectAsync(client);
        }

        // 76 entries (64 in the bag, 12 worn) at their ids' widths; the join frame also carries the world
        // descriptor, the roster and the first actors.
        output.WriteLine($"join {joinBytes} B, push {pushBytes} B, push with {InventoryPushMessage.MaxNotices} notices {loudestBytes} B, gate {GateBytes} B");
        Assert.True(joinBytes <= GateBytes, $"join {joinBytes} B");
        Assert.True(pushBytes <= GateBytes, $"push {pushBytes} B");
        Assert.True(loudestBytes <= GateBytes, $"push with every notice {loudestBytes} B");
    }

    // The same 76 entries with the widest ids an item can have (an ObjectId is positive 63-bit): 19 digits each.
    // The server's counter would take decades to get there, so this builds the snapshot directly.
    [Fact]
    public void SixtyFourRolledWithTwelveWorn_WithNineteenDigitIds_StillFitTheGate_InThePushWithEveryNotice()
    {
        List<InventorySlotSnapshot> bag = [];
        for (int i = 0; i < BagGrid.CellCount; i++)
        {
            bag.Add(new InventorySlotSnapshot(
                i,
                InventoryWireSamples.Item(
                    Filler[i % Filler.Length],
                    long.MaxValue - i,
                    ItemRarity.Rare,
                    rolled: true,
                    [new StatModifier(StatId.Vitality, StatOp.Flat, 6f)])));
        }

        List<InventorySlotSnapshot> worn = [];
        for (int i = 1; i < Worn.Length; i++)
        {
            worn.Add(new InventorySlotSnapshot(
                EquipmentSlots.FirstSlotIndex + i,
                InventoryWireSamples.Item(
                    Worn[i],
                    long.MaxValue - 100 - i,
                    ItemRarity.Rare,
                    rolled: true,
                    [
                        new StatModifier(StatId.Armor, StatOp.Flat, 5.25f),
                        new StatModifier(StatId.AttackSpeed, StatOp.Flat, 1.35f),
                    ]),
                IsEquipped: true));
        }

        PlayerInventorySnapshot inventory = new(
            new string('P', 24),
            8,
            8,
            bag,
            new InventorySlotSnapshot(
                EquipmentSlots.FirstSlotIndex,
                InventoryWireSamples.Item("gear.iron-blade", long.MaxValue - 99, ItemRarity.Rare, rolled: true, [new StatModifier(StatId.Strength, StatOp.Flat, 9f)]),
                IsEquipped: true),
            worn);
        InventoryPushMessage loudest = new(
            inventory,
            [.. Enumerable.Repeat(new LootNotice("Plated Leggings", ItemRarity.Rare, Lost: false), InventoryPushMessage.MaxNotices)]);

        int bytes = new TransportJsonCodec().Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, InventoryPushMessage.Kind, ulong.MaxValue),
            loudest).Length;

        output.WriteLine($"76 entries, 19-digit ids, {InventoryPushMessage.MaxNotices} notices: {bytes} B (gate {GateBytes} B)");
        Assert.True(bytes <= GateBytes, $"{bytes} B");
    }

    private static IEnumerable<InventoryPushMessage> Pushes(WireLoopbackClient client) =>
        client.Frames.Select(frame => frame.Decoded.Message).OfType<InventoryPushMessage>();
}
