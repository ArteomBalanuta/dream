using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// Wave I2 (I-D18 to I-D20): a kill's loot reaches the killer as an InventoryPushMessage over a real
// connection, before that tick's snapshot, to the killer only, and never as an action result (kills happen
// inside the tick golden, which hashes every result). A full backpack sends a notice and no snapshot.
[Collection(TimingSensitiveCollection.Name)]
public sealed class LootPushLoopbackTests
{
    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath));
        return await WireLoopbackServer.StartAsync(host);
    }

    private static IEnumerable<InventoryPushMessage> Pushes(WireLoopbackClient client) =>
        client.Frames.Select(frame => frame.Decoded.Message).OfType<InventoryPushMessage>();

    // Puts the wolf in front of the client with 1 HP and holds the attack until the client has a push.
    // Returns the server tick of the tick that produced it.
    private static async Task<ulong> KillUntilPushAsync(WireLoopbackServer server, WireLoopbackClient client)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            WaveIRuntimeKit.AimWolfAt(server.Runtime, client.SessionId, wolfHealth: 1);
            await client.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, Vector3.UnitZ, attack: true);
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
            ulong tick = server.Runtime.CurrentServerTick;
            if (server.Runtime.Enemies.ActiveActors.All(enemy => enemy.Combatant.Health.CurrentHealth != 1))
            {
                // The wolf we set to 1 HP is gone: killed (a fresh wolf spawns on the next tick).
                await client.WaitForSnapshotTickAsync(tick);
                if (Pushes(client).Any())
                {
                    return tick;
                }
            }
        }

        throw new Xunit.Sdk.XunitException("no kill produced a push");
    }

    private static async Task SettleFacingAsync(WireLoopbackServer server, WireLoopbackClient client)
    {
        // One command with the facing, so the wolf is aimed at along it.
        await client.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
        await server.TickAsync();
        Assert.True(await server.FlushAllAsync());
    }

    [Fact]
    public async Task AKill_PushesTheLootToTheKillerOnly_BeforeThatTicksSnapshot_AndNeverAsAnActionResult()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        server.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1);
        WireLoopbackClient killer = await server.ConnectAndJoinAsync("looter");
        WireLoopbackClient other = await server.ConnectAndJoinAsync("bystander");
        await SettleFacingAsync(server, killer);
        int itemsBefore = JoinedItemCount(killer);

        ulong pushTick = await KillUntilPushAsync(server, killer);
        await other.WaitForSnapshotTickAsync(pushTick);

        InventoryPushMessage push = Assert.Single(Pushes(killer));
        LootNotice notice = Assert.Single(push.Notices);
        Assert.False(notice.Lost);
        Assert.NotNull(push.Inventory);
        Assert.Equal(itemsBefore + 1, push.Inventory!.Value.Slots.Count(slot => slot.Item != null));
        Assert.Contains(push.Inventory.Value.Slots, slot => slot.Item is { IsRolled: true });
        Assert.Empty(Pushes(other));

        // Before that tick's snapshot, after the previous one.
        IReadOnlyList<WireFrame> frames = killer.Frames;
        int pushIndex = frames.ToList().FindIndex(frame => frame.Decoded.Message is InventoryPushMessage);
        int SnapshotIndex(ulong tick) => frames.ToList().FindIndex(
            frame => frame.Decoded.Message is ServerSnapshotMessage snapshot && snapshot.Snapshot.ServerTick == tick);
        Assert.True(SnapshotIndex(pushTick) > pushIndex, "the push must come before the snapshot of the tick that made it");
        Assert.True(SnapshotIndex(pushTick - 1) < pushIndex, "the push must come after the previous tick's snapshot");

        // The loot is in no action result: the kill's own result is a combat resolution.
        List<ServerActionMessage> results = [.. frames.Select(frame => frame.Decoded.Message).OfType<ServerActionMessage>()];
        Assert.Contains(results, result => result.Result.Payload is CombatResolutionResult { TargetDied: true });
        Assert.All(results, result => Assert.IsNotType<InventoryChangedResult>(result.Result.Payload));
    }

    [Fact]
    public async Task AFullBackpack_SendsALostNoticeWithNoSnapshot_InAFewHundredBytes()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        server.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1);
        WireLoopbackClient killer = await server.ConnectAndJoinAsync("packrat");
        await SettleFacingAsync(server, killer);
        string key = WorldSessionRuntime.ResolveJoinPlayerName("packrat", "Name packrat");
        int seed = 0;
        while (server.Runtime.InventoryService.TryGrantRolled(key, "gear.leather-cap", ItemRarity.Common, new ItemRollSeed(3, 3, 3, 3, seed++)) == InventoryEquipOutcome.Equipped)
        {
            Assert.True(seed < 100, "the backpack never filled");
        }

        await KillUntilPushAsync(server, killer);

        WireFrame frame = killer.Frames.Single(f => f.Decoded.Message is InventoryPushMessage);
        InventoryPushMessage push = Assert.IsType<InventoryPushMessage>(frame.Decoded.Message);
        Assert.Null(push.Inventory);
        Assert.True(Assert.Single(push.Notices).Lost);
        Assert.True(frame.Payload.Length <= 256, $"a lost-only push is {frame.Payload.Length} B");
    }

    private static int JoinedItemCount(WireLoopbackClient client) =>
        client.Frames.Select(frame => frame.Decoded.Message).OfType<JoinAcceptedMessage>().Last().Inventory!.Value.Slots.Count(slot => slot.Item != null);
}
