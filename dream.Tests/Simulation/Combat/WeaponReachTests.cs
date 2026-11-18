using System.Globalization;
using System.IO;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.Simulation.Combat;

// The per-weapon attack reach: the authored values, their validation, the equipped-item cache and the stats
// rows that show it.
public sealed class WeaponReachTests
{
    private static string ShippedItemsJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.ItemsFileName));

    private static string ShippedEnemiesJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.EnemiesFileName));

    [Theory]
    [InlineData("gear.iron-dagger", 1.5f)]
    [InlineData("gear.short-sword", 1.7f)]
    [InlineData("gear.hunting-spear", 2.5f)]
    [InlineData("gear.greatsword", 2.2f)]
    [InlineData("gear.short-bow", 10f)]
    [InlineData("gear.long-bow", 14f)]
    public void ShippedWeapons_CarryTheirAuthoredReach(string kind, float reach)
    {
        Assert.Equal(reach, WeaponReach.ForItemKind(kind));
    }

    [Fact]
    public void EmptyHandUnknownKindAndAnItemWithoutAReach_AreUnarmed()
    {
        Assert.Equal(WeaponReach.UnarmedMetres, WeaponReach.ForItemKind(null));
        Assert.Equal(WeaponReach.UnarmedMetres, WeaponReach.ForItemKind("tool.no-such-item"));
        Assert.Equal(WeaponReach.UnarmedMetres, WeaponReach.ForItemKind("gear.leather-cap"));
        Assert.Equal(WeaponReach.UnarmedMetres, WeaponReach.ForItem(null));
    }

    [Fact]
    public void EveryShippedMainHandItem_HasAReachInsideTheAllowedRange()
    {
        int checkedItems = 0;
        foreach (ItemDefinition item in ContentCatalog.Shipped.Items)
        {
            if ((item.EquipSlotMask & EquipmentSlots.MaskOf(EquipmentSlot.MainHand)) == 0)
            {
                Assert.Null(item.AttackRange);
                continue;
            }

            checkedItems++;
            Assert.True(item.AttackRange.HasValue, $"{item.Key} has no attackRange");
            Assert.InRange(item.AttackRange.Value, WeaponReach.MinMetres, WeaponReach.MaxMetres);
        }

        Assert.True(checkedItems > 10, "the loop must have seen the weapons");
    }

    [Fact]
    public void TheMinimumReach_IsDerivedFromTheWolfsStandOff_AndUnarmedClearsIt()
    {
        Assert.True(WeaponReach.MinMetres > Dream.Simulation.Enemies.WolfDefinition.PreferredStandOffDistance);
        Assert.True(WeaponReach.UnarmedMetres >= WeaponReach.MinMetres);
        Assert.True(WeaponReach.UnarmedMetres * WeaponReach.ClientApproachFraction > Dream.Simulation.Enemies.WolfDefinition.PlayerSeparationDistance);
    }

    [Fact]
    public void AttackRange_OnAnItemThatIsNotMainHand_IsRejected()
    {
        string items = ShippedItemsJson.Replace("\"key\": \"gear.leather-cap\",", "\"key\": \"gear.leather-cap\",\n      \"attackRange\": 2.0,");

        ContentValidationException exception = Assert.Throws<ContentValidationException>(() => ContentCatalog.Load(items, ShippedEnemiesJson));

        Assert.Contains("attackRange", exception.Message);
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("25")]
    [InlineData("\"far\"")]
    public void AttackRange_OutsideTheAllowedRange_IsRejected(string value)
    {
        string items = ShippedItemsJson.Replace("\"attackRange\": 1.5,", "\"attackRange\": " + value + ",");
        Assert.NotEqual(ShippedItemsJson, items);

        Assert.Throws<ContentValidationException>(() => ContentCatalog.Load(items, ShippedEnemiesJson));
    }

    [Fact]
    public void TheDigest_ChangesWithAReach()
    {
        string changed = ShippedItemsJson.Replace("\"attackRange\": 1.5,", "\"attackRange\": 1.6,");
        string loot = File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.LootFileName));

        Assert.NotEqual(
            ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson, loot).ContentDigest,
            ContentCatalog.Load(changed, ShippedEnemiesJson, loot).ContentDigest);
    }

    [Fact]
    public void TheEquippedReach_FollowsTheMainHand_AndIsUnarmedWhenEmpty()
    {
        DateTime now = DateTime.UtcNow;
        PersistedItemInstance dagger = new(
            7, "gear.iron-dagger", "Dagger", "held.weapon.dagger", "Zed", WorldPosition: null, State: "Inventory", StackCount: 1, now, now);
        PlayerInventoryService armed = new(worldPersistence: null, writes: null, objectIds: new ObjectIdAllocator());
        armed.ApplyLoaded("Zed", new PersistedPlayerInventory(
            [new PersistedInventorySlot("Zed", InventoryConstants.EquippedSlotIndex, 7, now)],
            [dagger]));
        PlayerInventoryService bare = new(worldPersistence: null, writes: null, objectIds: new ObjectIdAllocator());
        bare.ApplyLoaded("Zed", new PersistedPlayerInventory([], []));

        Assert.Equal(WeaponReach.ForItemKind("gear.iron-dagger"), armed.ResolveEquippedAttackReach("Zed"));
        Assert.Equal(WeaponReach.UnarmedMetres, bare.ResolveEquippedAttackReach("Zed"));
        Assert.Equal(WeaponReach.UnarmedMetres, bare.ResolveEquippedAttackReach("nobody"));
    }

    [Fact]
    public void TheStatsSheet_ListsReachAndSpeedsInOrder_ReadingTheTuningConstants()
    {
        HudStatusSnapshot status = new(50, 100, PlayerInteractionMode.Combat, 1, 2, 5, 1f, 5, 1, 0, 100) { AttackRange = 2.5f };

        CharacterSheetRow[] rows = CharacterSheetModel.Build(status);
        string[] labels = rows.Select(r => r.Label).ToArray();

        Assert.Equal(labels.IndexOf("Attack Speed") + 1, labels.IndexOf("Attack Range"));
        Assert.Equal(labels.IndexOf("Magic Damage") + 1, labels.IndexOf("Move Speed"));
        Assert.Equal(labels.IndexOf("Move Speed") + 1, labels.IndexOf("Run Speed"));
        Assert.Equal("2.5 m", rows[labels.IndexOf("Attack Range")].Value);
        Assert.Equal(
            PlayerSimulationTuning.AuthoritativeDefault.MoveSpeed.ToString("0.00", CultureInfo.InvariantCulture) + " m/s",
            rows[labels.IndexOf("Move Speed")].Value);
        Assert.Equal(
            PlayerSimulationTuning.AuthoritativeDefault.SprintSpeed.ToString("0.00", CultureInfo.InvariantCulture) + " m/s",
            rows[labels.IndexOf("Run Speed")].Value);
        Assert.True(rows.Length < 20, "the window holds at most 20 rows");
    }

    [Fact]
    public void TheStatsSheet_ShowsTheUnarmedReach_WhenNoneIsKnown()
    {
        HudStatusSnapshot status = new(50, 100, PlayerInteractionMode.Combat, 1, 2, 5, 1f, 5, 1, 0, 100);

        CharacterSheetRow row = CharacterSheetModel.Build(status).Single(r => r.Label == "Attack Range");

        Assert.Equal(WeaponReach.UnarmedMetres.ToString("0.0#", CultureInfo.InvariantCulture) + " m", row.Value);
    }
}
