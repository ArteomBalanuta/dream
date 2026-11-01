using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;

namespace Dream.Tests.Client.Scenes;

// Wave I3: what an InventoryPushMessage does on the client. PlayScene needs a GL context, so these run
// the pieces its push branch calls: the equipment reader, the combatant's equipment step and the loot
// feed. A push carries no health or position, so neither can change; the tests pin that on the
// combatant. The scene-level branch (ApplyReplicatedMessage) is covered at glue by the loopback flow.
public sealed class PlaySceneInventoryPushTests
{
    private static InventoryItemSnapshot Hostile(IReadOnlyList<StatModifier>? modifiers, string name = "Cap") => new(
        1, "gear.leather-cap", name, "visual", 1, "d", [], modifiers, HeldLightKind.None, EquipSlots: 1 << 0);

    private static PlayerInventorySnapshot InventoryWith(InventoryItemSnapshot item) =>
        new("p", 4, 4, [], new InventorySlotSnapshot(100, item, IsEquipped: true));

    [Fact]
    public void AHostilePush_With1e99Modifiers_And40Notices_DoesNotThrow_AndShowsAtMost8()
    {
        InventoryItemSnapshot item = Hostile(
        [
            new StatModifier(StatId.Strength, StatOp.Flat, (float)1e99),
            new StatModifier(StatId.Armor, StatOp.Flat, float.MaxValue),
            new StatModifier(StatId.AttackSpeed, StatOp.Flat, float.NaN),
            new StatModifier((StatId)250, StatOp.Flat, 1f)
        ]);
        LootNotice[] notices = Enumerable.Range(0, 40)
            .Select(i => new LootNotice($"Item {i}", (ItemRarity)(i % 7), Lost: i % 2 == 0))
            .ToArray();
        InventoryPushMessage push = new(InventoryWith(item), notices);
        PlayerCombatant player = new(() => Vector3.Zero);
        int healthBefore = player.Health.CurrentHealth;
        int maxBefore = player.Health.MaxHealth;
        LootFeedbackSystem feed = new();

        EquipmentLoadout loadout = new();
        Exception? thrown = Record.Exception(() =>
        {
            PlayScene.ReadEquipment(push.Inventory, loadout);
            player.ApplyEquipment(loadout);
            feed.AddNotices(push.Notices);
        });

        Assert.Null(thrown);
        Assert.Equal(healthBefore, player.Health.CurrentHealth);
        Assert.True(player.Health.CurrentHealth <= player.Health.MaxHealth);
        Assert.True(maxBefore > 0);
        Assert.True(feed.Count <= LootFeedbackSystem.MaxLines);
        Assert.True(feed.Count <= InventoryPushMessage.MaxNotices);
    }

    [Fact]
    public void ANullInventoryPush_OnlyAddsNotices()
    {
        LootFeedbackSystem feed = new();
        InventoryPushMessage push = new(null, [new LootNotice("Leather Cap", ItemRarity.Common, Lost: true)]);

        feed.AddNotices(push.Notices);

        Assert.Equal("Backpack full: Leather Cap was lost", feed.GetLine(0));
    }

    [Fact]
    public void InventoryText_KeepsPercentLiteral()
    {
        InventoryPushMessage push = new(
            InventoryWith(Hostile(null, "100% Cap")),
            [new LootNotice("100% {0} Cap", ItemRarity.Rare, Lost: false)]);
        LootFeedbackSystem feed = new();

        feed.AddNotices(push.Notices);

        Assert.Equal("Looted: 100% {0} Cap (Rare)", feed.GetLine(0));
        Assert.Equal("100% Cap", push.Inventory!.Value.EquippedSlot!.Value.Item!.Value.DisplayName);
    }
}
