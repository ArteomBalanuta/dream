using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Integration;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Client.Scenes;

// M3 on the client: the ground-item cache and the E key, the vendor window's state and the economy
// intents, with a real PlayScene (no GL) behind a loopback server for the scene-level cases. The cache
// tests need no scene: the wire is hostile and the cache is where it is checked.
[Collection(TimingSensitiveCollection.Name)]
public sealed class PlaySceneGroundItemsTests
{
    private const string Session = "ground-items";
    private const string Torch = "tool.torch";
    private const string Blade = "tool.field-blade";

    private static GroundItemSnapshot Item(long id, string kind = Torch, Vector3? at = null, int claimant = 0, ulong expires = 0) =>
        new(id, kind, ItemRarity.Common, at ?? new Vector3(1f, 0f, 1f), claimant, expires);

    private static ServerGroundItems Message(bool reset, GroundItemSnapshot[] upserts, long[]? removed = null) =>
        new(reset, upserts, removed ?? []);

    [Fact]
    public void AResetReplacesTheList_AnUpsertReplacesById_ARemovalDrops_AndAllAreIdempotent()
    {
        ClientGroundItemCache cache = new();

        cache.Apply(Message(true, [Item(1), Item(2)]));
        cache.Apply(Message(false, [Item(2, Blade)], [1]));
        cache.Apply(Message(false, [Item(2, Blade)], [1]));

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(2, out ClientGroundItem item));
        Assert.Equal(Blade, item.ItemKind);

        cache.Apply(Message(true, [Item(7)]));
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(7, out _));
    }

    [Fact]
    public void TheNameIsTheCatalogsNotTheWires()
    {
        ClientGroundItemCache cache = new();

        cache.Apply(Message(true, [Item(1)]));

        Assert.True(Dream.Simulation.Content.ContentCatalog.Shipped.TryGetItem(Torch, out var definition));
        Assert.Equal(definition.DisplayName, cache[0].DisplayName);
    }

    [Fact]
    public void HostileEntriesAreDroppedAndTheRestKept()
    {
        ClientGroundItemCache cache = new();

        cache.Apply(Message(true,
        [
            Item(0),
            Item(-5),
            Item(3, "no.such.item"),
            Item(4, new string('x', 5000)),
            Item(5, at: new Vector3(float.NaN, 0f, 0f)),
            Item(6, at: new Vector3(1e30f, 0f, 0f)),
            Item(8, at: new Vector3(float.PositiveInfinity, 0f, 0f)),
            new GroundItemSnapshot(9, Torch, (ItemRarity)200, new Vector3(2f, 0f, 2f), -4, 77),
            Item(10)
        ]));

        Assert.Equal(2, cache.Count);
        Assert.Equal(7, cache.RejectedCount);
        Assert.True(cache.TryGet(9, out ClientGroundItem odd));
        Assert.Equal(ItemRarity.Common, odd.Rarity);
        Assert.Equal(0, odd.ClaimantActorId);
        Assert.Equal(0UL, odd.ClaimExpiresTick);
    }

    [Fact]
    public void TheListNeverPassesItsCap_AndNullListsAreSafe()
    {
        ClientGroundItemCache cache = new();
        for (int round = 0; round < 8; round++)
        {
            GroundItemSnapshot[] batch = Enumerable.Range(1, ServerGroundItems.MaxEntries * 2)
                .Select(n => Item((round * 1000) + n))
                .ToArray();
            cache.Apply(Message(round == 0, batch));
        }

        Assert.True(cache.Count <= ClientGroundItemCache.MaxItems);

        cache.Apply(new ServerGroundItems(false, null!, null!));
        cache.Apply(null);

        Assert.True(cache.Count <= ClientGroundItemCache.MaxItems);
    }

    [Fact]
    public void AClaimIsAPair_AndOnlyHoldsOthersOffUntilItExpires()
    {
        ClientGroundItemCache cache = new();
        cache.Apply(Message(true,
        [
            Item(1, claimant: 55, expires: 100),
            Item(2, claimant: 55, expires: 0),
            Item(3, claimant: 0, expires: 100)
        ]));

        ClientGroundItem held = cache[0];
        Assert.True(ClientGroundItemCache.IsHeldBack(in held, localActorId: 9, serverTick: 99));
        Assert.False(ClientGroundItemCache.IsHeldBack(in held, localActorId: 9, serverTick: 100));
        Assert.False(ClientGroundItemCache.IsHeldBack(in held, localActorId: 55, serverTick: 1));
        Assert.Equal(0, cache[1].ClaimantActorId);
        Assert.Equal(0UL, cache[2].ClaimExpiresTick);
    }

    [Fact]
    public void TheNearestFreeItemInRangeWins()
    {
        ClientGroundItemCache cache = new();
        cache.Apply(Message(true,
        [
            Item(1, at: new Vector3(3f, 0f, 0f)),
            Item(2, at: new Vector3(1f, 0f, 0f), claimant: 9, expires: 1000),
            Item(3, at: new Vector3(2f, 0f, 0f)),
            Item(4, at: new Vector3(50f, 0f, 0f))
        ]));

        Assert.True(cache.TryGetNearest(Vector3.Zero, ClientGroundItemCache.PickupRange, localActorId: 1, serverTick: 10, out ClientGroundItem nearest));
        Assert.Equal(3, nearest.ItemInstanceId);
        Assert.False(cache.TryGetNearest(new Vector3(100f, 0f, 100f), ClientGroundItemCache.PickupRange, 1, 10, out _));
    }

    [Fact]
    public void TheNpcCacheKeepsOnlyNpcActors_AndCleansTheWire()
    {
        ClientNpcCache npcs = new();
        List<ActorSnapshot> actors =
        [
            Actor(ReplicatedActorIdRanges.FirstNpcId, CreatureKind.Npc, "Old\u0007 Mara"),
            Actor(ReplicatedActorIdRanges.FirstNpcId, CreatureKind.Npc, "Twin"),
            Actor(5, CreatureKind.Enemy, "Wolf"),
            Actor(ReplicatedActorIdRanges.FirstNpcId + 1, CreatureKind.Npc, new string('n', 400)),
            Actor(ReplicatedActorIdRanges.FirstNpcId + 2, CreatureKind.Npc, "Nowhere", new Vector3(float.NaN, 0f, 0f)),
            Actor(0, CreatureKind.Npc, "Zero")
        ];

        npcs.ApplyActors(actors);

        Assert.Equal(2, npcs.Count);
        Assert.True(npcs.TryGet(ReplicatedActorIdRanges.FirstNpcId, out ClientNpc mara));
        Assert.Equal("Old Mara", mara.DisplayName);
        Assert.True(npcs[1].DisplayName.Length <= ClientNpcCache.MaxNameChars);

        npcs.ApplyActors([Actor(5, CreatureKind.Enemy, "Wolf")]);
        Assert.Equal(0, npcs.Count);
    }

    private static ActorSnapshot Actor(int id, CreatureKind kind, string? name, Vector3? at = null) =>
        new(id, kind, null, at ?? new Vector3(6f, 0f, 5f), -Vector3.UnitZ, 1, 1, true, DisplayName: name);

    // ---- scene level ----

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        return await WireLoopbackServer.StartAsync(host);
    }

    // Waits until the server has read an action whose payload satisfies the check, and returns it.
    private static async Task<IPlayerActionPayload> ReadActionAsync(WireLoopbackServer server, HeadlessPlayScene client, Func<IPlayerActionPayload, bool> match)
    {
        Assert.True(await client.Session.FlushOutboundAsync(WireLoopbackServer.Timeout));
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (true)
        {
            if (server.Host.TryGetConnectionContext(client.Connection.ConnectionId, out ServerRoutingContext? context)
                && context?.LastActionMessage?.Payload is { } payload
                && match(payload))
            {
                return payload;
            }

            Assert.True(DateTime.UtcNow < deadline, "The server never read the expected action.");
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task GroundItemsApplyBeforeTheJoinCompletes_AndTheJoinDoesNotClearThem()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session, pumpJoin: false);

        // A delta that beats the join, then the join: the list stays (a Reset message clears, a join does not).
        client.Scene.ApplyReplicatedMessage(Message(false, [Item(1), Item(2)]));
        await client.PumpUntilAsync(c => c.Applied.Any(m => m is JoinAcceptedMessage), "the join");

        Assert.Equal(2, client.GroundItems.Count);
        client.Scene.ApplyReplicatedMessage(Message(true, [Item(3)]));
        Assert.Equal(1, client.GroundItems.Count);
    }

    [Fact]
    public async Task TheEKeyTalksToTheNearestNpc_ElseTakesTheNearestFreeItem()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session);
        Vector3 here = client.World.Player.GetWorldPosition();

        Assert.False(client.Scene.TryInteractWithNearest());

        client.GroundItems.Apply(Message(true,
        [
            Item(11, at: here + new Vector3(1f, 0f, 0f), claimant: 123456, expires: ulong.MaxValue),
            Item(12, at: here + new Vector3(2f, 0f, 0f))
        ]));
        Assert.True(client.Scene.TryInteractWithNearest());
        IPlayerActionPayload pickup = await ReadActionAsync(server, client, p => p is GroundPickupIntent);
        Assert.Equal(12, ((GroundPickupIntent)pickup).ItemInstanceId);

        client.Npcs.ApplyActors([Actor(ReplicatedActorIdRanges.FirstNpcId, CreatureKind.Npc, "Mara", here + new Vector3(0f, 0f, 3f))]);
        Assert.True(client.Scene.TryInteractWithNearest());
        IPlayerActionPayload talk = await ReadActionAsync(server, client, p => p is NpcInteractIntent);
        Assert.Equal(ReplicatedActorIdRanges.FirstNpcId, ((NpcInteractIntent)talk).NpcActorId);
    }

    [Fact]
    public async Task AVendorResult_OpensTheWindowAndInventory_FiltersHostileStock_AndAUnknownNpcIsIgnored()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session);
        Vector3 here = client.World.Player.GetWorldPosition();
        int npcId = ReplicatedActorIdRanges.FirstNpcId;
        VendorStockEntry[] stock =
        [
            new(Torch, 12),
            new(Torch, 13),
            new(Blade, 0),
            new("no.such.item", 5),
            new(Blade, long.MaxValue),
            new(Blade, 40)
        ];

        client.Scene.ApplyReplicatedMessage(VendorResult(client, npcId, "Mara", stock));
        Assert.False(client.Vendor.IsOpen);

        client.Npcs.ApplyActors([Actor(npcId, CreatureKind.Npc, "Mara", here + new Vector3(0f, 0f, 3f))]);
        client.Scene.ApplyReplicatedMessage(VendorResult(client, npcId, "Mara\u0001", stock));

        Assert.True(client.Vendor.IsOpen);
        Assert.Equal(npcId, client.Vendor.NpcActorId);
        Assert.Equal(2, client.Vendor.StockCount);
        Assert.Equal(12, client.Vendor.StockAt(0).Price);
        Assert.Equal(Blade, client.Vendor.StockAt(1).ItemKind);
        Assert.Equal("Mara", client.Vendor.Title);
        Assert.True(Settings(client).InventoryVisible);
    }

    [Fact]
    public async Task VendorPicksBecomeIntents_OnlyForTheOpenVendorsStock_AndLeavingRangeClosesIt()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session);
        Vector3 here = client.World.Player.GetWorldPosition();
        int npcId = ReplicatedActorIdRanges.FirstNpcId;
        client.Npcs.ApplyActors([Actor(npcId, CreatureKind.Npc, "Mara", here + new Vector3(0f, 0f, 3f))]);
        client.Scene.ApplyReplicatedMessage(VendorResult(client, npcId, "Mara", [new VendorStockEntry(Torch, 12)]));
        Assert.True(client.Vendor.IsOpen);

        client.Scene.ApplyEconomyRequest(HudInteractionRequest.None with
        {
            Vendor = new VendorHudAction(VendorHudActionKind.Buy, Blade)
        });
        client.Scene.ApplyEconomyRequest(HudInteractionRequest.None with
        {
            Vendor = new VendorHudAction(VendorHudActionKind.Buy, Torch)
        });
        IPlayerActionPayload buy = await ReadActionAsync(server, client, p => p is VendorBuyIntent);
        Assert.Equal(new VendorBuyIntent(npcId, Torch), buy);

        client.Scene.ApplyEconomyRequest(HudInteractionRequest.None with
        {
            Vendor = new VendorHudAction(VendorHudActionKind.Sell, SlotIndex: 3, ItemInstanceId: 42)
        });
        IPlayerActionPayload sell = await ReadActionAsync(server, client, p => p is VendorSellIntent);
        Assert.Equal(new VendorSellIntent(npcId, 3, 42), sell);

        client.Npcs.ApplyActors([Actor(npcId, CreatureKind.Npc, "Mara", here + new Vector3(0f, 0f, 40f))]);
        client.Scene.UpdateEconomyState();

        Assert.False(client.Vendor.IsOpen);
    }

    private static Dream.Application.GameSettings Settings(HeadlessPlayScene client) =>
        (Dream.Application.GameSettings)typeof(Dream.Client.Scenes.PlayScene)
            .GetField("_settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client.Scene)!;

    private static ServerActionMessage VendorResult(HeadlessPlayScene client, int npcId, string name, VendorStockEntry[] stock) =>
        new(new AuthoritativeActionResult(client.SessionId, 1, 1, true, new VendorOpenedResult(npcId, name, stock)));
}
