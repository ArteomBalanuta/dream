using Xunit;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Dream.Server;
using Dream.Server.Hosting;

namespace Dream.Tests.Integration;

// Potions: the server removes the item and applies the effect, and rejects (consuming nothing) when
// the pool is already full. Plus the pure pieces: HealthState.Restore, the catalogue entries, the bag
// menu and the Q/E lookup.
[Collection(TimingSensitiveCollection.Name)]
public sealed class ConsumableUseTests
{
    private const string Session = "consumable-use";

    [Fact]
    public void HealthState_Restore_HealsTheLivingUpToMax_AndNeverTheDead()
    {
        HealthState health = new(100);
        health.ApplyDamage(30);
        Assert.Equal(20, health.Restore(20));
        Assert.Equal(90, health.CurrentHealth);
        Assert.Equal(10, health.Restore(500));
        Assert.Equal(100, health.CurrentHealth);
        Assert.Equal(0, health.Restore(5));
        Assert.Equal(0, health.Restore(-5));

        health.ApplyDamage(1000);
        Assert.Equal(0, health.Restore(50));
        Assert.False(health.IsAlive);
    }

    [Fact]
    public void ThePotions_AreNonStackingOneByOneConsumables_WithNoEquipSlots()
    {
        string[] kinds = ["consumable.weak-heal", "consumable.minor-heal", "consumable.greater-heal", "consumable.minor-mana", "consumable.greater-mana"];
        foreach (string kind in kinds)
        {
            Assert.True(ContentCatalog.Shipped.TryGetItem(kind, out ItemDefinition? item));
            Assert.NotNull(item.Use);
            Assert.Empty(item.EquipSlots);
            Assert.Equal(ItemFootprint.One, item.Footprint);
            Assert.NotNull(item.Rolls);
        }

        ContentCatalog.Shipped.TryGetItem("consumable.greater-heal", out ItemDefinition? greater);
        Assert.Equal(new ItemUse(ItemUseEffect.RestoreHealth, 100), greater!.Use);
        ContentCatalog.Shipped.TryGetItem("consumable.minor-mana", out ItemDefinition? mana);
        Assert.Equal(new ItemUse(ItemUseEffect.RestoreMana, 30), mana!.Use);
    }

    [Fact]
    public void ARejectedUseOfAnItemWithoutUse_AndTheLoaderRules()
    {
        string items = File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.ItemsFileName));
        string enemies = File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.EnemiesFileName));
        string loot = File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.LootFileName));
        string badEffect = items.Replace("\"effect\": \"RestoreMana\"", "\"effect\": \"Explode\"");
        Assert.Contains("unknown use effect", Assert.Throws<ContentValidationException>(() => ContentCatalog.Load(badEffect, enemies, loot)).Message);
        string badAmount = items.Replace("\"amount\": 30", "\"amount\": 0");
        Assert.Throws<ContentValidationException>(() => ContentCatalog.Load(badAmount, enemies, loot));
        string changed = items.Replace("\"amount\": 30", "\"amount\": 31");
        Assert.NotEqual(ContentCatalog.Shipped.ContentDigest, ContentCatalog.Load(changed, enemies, loot).ContentDigest);
    }

    [Fact]
    public void TheBagMenu_OffersUse_ForAPotion_AndTheQuickSlotsFindTheLowestAnchor()
    {
        PlayerInventorySnapshot inventory = new(
            "p",
            8,
            8,
            [
                new InventorySlotSnapshot(9, new InventoryItemSnapshot(2, "consumable.minor-heal", "Minor Healing Draught", "consumable.potion.heal", 1, "", [])),
                new InventorySlotSnapshot(3, new InventoryItemSnapshot(3, "consumable.greater-heal", "Greater Healing Draught", "consumable.potion.heal", 1, "", [])),
                new InventorySlotSnapshot(20, new InventoryItemSnapshot(4, "consumable.minor-mana", "Minor Mana Draught", "consumable.potion.mana", 1, "", [])),
            ]);

        Assert.True(CharacterPanelModel.TryFindConsumable(inventory, ItemUseEffect.RestoreHealth, out int anchor, out string kind, out int count));
        Assert.Equal(3, anchor);
        Assert.Equal("consumable.greater-heal", kind);
        Assert.Equal(2, count);
        Assert.True(CharacterPanelModel.TryFindConsumable(inventory, ItemUseEffect.RestoreMana, out anchor, out _, out count));
        Assert.Equal((20, 1), (anchor, count));

        InventoryItemSnapshot potion = inventory.Slots[0].Item!.Value;
        List<CharacterMenuEntry> menu = CharacterPanelModel.BackpackMenu(inventory, 9, potion);
        Assert.Equal("Use", menu[0].Label);
        Assert.Equal(InventoryHudActionKind.EquipTo, menu[0].Action.Kind);
        Assert.Equal(CharacterPanelModel.FirstFitCell, menu[0].Action.Target);
        Assert.DoesNotContain(menu, entry => entry.Label.StartsWith("Equip", StringComparison.Ordinal));
    }

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        return await WireLoopbackServer.StartAsync(host);
    }

    private static PlayerInventorySnapshot JoinInventory(WireLoopbackClient client) =>
        client.Frames.Select(frame => frame.Decoded.Message).OfType<JoinAcceptedMessage>().Last().Inventory!.Value;

    private static async Task<ServerActionMessage> SendAsync(WireLoopbackServer server, WireLoopbackClient client, IPlayerActionPayload payload)
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
                return result;
            }

            Assert.True(DateTime.UtcNow < deadline, $"action {sequence} was never answered");
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task APotion_HealsOnUse_IsConsumedAndPersisted_AndIsRefusedAtFullHealth()
    {
        using MigrationTestDatabase db = new();
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            PlayerInventorySnapshot inventory = JoinInventory(client);
            Assert.True(CharacterPanelModel.TryFindConsumable(inventory, ItemUseEffect.RestoreHealth, out int anchor, out string kind, out int count));
            Assert.Equal(2, count);
            ContentCatalog.Shipped.TryGetItem(kind, out ItemDefinition? definition);
            int amount = definition!.Use!.Amount;

            HealthState health = server.Runtime.Player.Combatant.Health;
            Assert.Equal(health.MaxHealth, health.CurrentHealth);

            // Full health: refused, nothing consumed.
            ServerActionMessage refused = await SendAsync(server, client, new InventoryEquipIntent(anchor));
            Assert.False(refused.Result.Accepted);

            health.ApplyDamage(health.MaxHealth - 1);
            int before = health.CurrentHealth;
            ServerActionMessage accepted = await SendAsync(server, client, new InventoryEquipIntent(anchor, CharacterPanelModel.FirstFitCell));
            Assert.True(accepted.Result.Accepted, accepted.Result.Rejection?.ToString());
            PlayerInventorySnapshot after = Assert.IsType<InventoryChangedResult>(accepted.Result.Payload).Inventory;
            Assert.Equal(Math.Min(health.MaxHealth, before + amount), health.CurrentHealth);
            Assert.Null(after.ItemAt(anchor));
            Assert.True(CharacterPanelModel.TryFindConsumable(after, ItemUseEffect.RestoreHealth, out _, out _, out int left));
            Assert.Equal(1, left);
            await server.DisconnectAsync(client);
        }

        await using (WireLoopbackServer restarted = await StartAsync(db))
        {
            WireLoopbackClient client = await restarted.ConnectAndJoinAsync(Session);
            Assert.True(CharacterPanelModel.TryFindConsumable(JoinInventory(client), ItemUseEffect.RestoreHealth, out _, out _, out int persisted));
            Assert.Equal(1, persisted);
            await restarted.DisconnectAsync(client);
        }
    }
}
