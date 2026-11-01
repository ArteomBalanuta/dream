using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// Wave I glue (I-D26): the largest inventory a player can reach is a backpack of 16 rare rolled items with
// 4 of them worn, and a worn item travels twice (in its slot and in the equipment list). The real join
// frame and the real push of the last item stay within the gate, and so does that push with the most
// notices one can carry. If this fails, trim as I-D26 says; do not raise the gate without recording why.
[Collection(TimingSensitiveCollection.Name)]
public sealed class WorstCaseInventoryWireSizeTests(ITestOutputHelper output)
{
    private const string Session = "worst-case";
    private const int GateBytes = 16_000;
    private static readonly string[] Gear = ["gear.iron-blade", "gear.ward-lantern", "gear.leather-cap", "gear.leather-vest"];

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath));
        return await WireLoopbackServer.StartAsync(host);
    }

    [Fact]
    public async Task SixteenRolledWithFourWorn_FitTheGate_InTheJoinAndInThePush()
    {
        using MigrationTestDatabase db = new();
        string key = WorldSessionRuntime.ResolveJoinPlayerName(Session, $"Name {Session}");
        int pushBytes;
        InventoryPushMessage push;
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            server.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1, common: 0, uncommon: 0, rare: 1);
            WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            PlayerInventoryService inventory = server.Runtime.InventoryService;

            // Only rolled items: the starter kit goes, then 15 rare items, the last comes from a kill.
            Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryUnequip(key, inventory.BuildSnapshot(key).EquippedSlot!.Value.SlotIndex));
            foreach (InventorySlotSnapshot slot in inventory.BuildSnapshot(key).Slots.Where(slot => slot.Item != null))
            {
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryDestroy(key, slot.SlotIndex, slot.Item!.Value.ItemInstanceId));
            }

            int capacity = inventory.BuildSnapshot(key).Slots.Count;
            Assert.Equal(16, capacity);
            for (int i = 0; i < capacity - 1; i++)
            {
                Assert.Equal(
                    InventoryEquipOutcome.Equipped,
                    inventory.TryGrantRolled(key, Gear[i % Gear.Length], ItemRarity.Rare, new ItemRollSeed(7, 7, 7, 7, i)));
            }

            foreach (string kind in Gear)
            {
                InventorySlotSnapshot slot = inventory.BuildSnapshot(key).Slots.First(s => s.Item?.ItemKind == kind);
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryEquip(key, slot.SlotIndex));
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
        Assert.Equal(16, full.Slots.Count(slot => slot.Item is { IsRolled: true }));
        Assert.Equal(3, full.Equipment!.Count(row => row.Item != null));
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
            joinBytes = client.Frames.First(f => f.Decoded.Message is JoinAcceptedMessage).Payload.Length;
            await server.DisconnectAsync(client);
        }

        output.WriteLine($"join {joinBytes} B, push {pushBytes} B, push with {InventoryPushMessage.MaxNotices} notices {loudestBytes} B, gate {GateBytes} B");
        Assert.True(joinBytes <= GateBytes, $"join {joinBytes} B");
        Assert.True(pushBytes <= GateBytes, $"push {pushBytes} B");
        Assert.True(loudestBytes <= GateBytes, $"push with every notice {loudestBytes} B");
    }

    private static IEnumerable<InventoryPushMessage> Pushes(WireLoopbackClient client) =>
        client.Frames.Select(frame => frame.Decoded.Message).OfType<InventoryPushMessage>();
}
