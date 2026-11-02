using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Xunit;

namespace Dream.Tests.Simulation.Content;

// M4: config/content/skills.json (skills and the default quickbar) and the "use" object of a consumable
// item. The file is optional; every limit of the contract is a load error, not a clamp.
public sealed class SkillContentTests
{
    private static string Shipped(string file) => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, file));

    private static ContentCatalog LoadWith(string skillsJson, string? itemsJson = null) =>
        ContentCatalog.Load(
            itemsJson ?? Shipped(ContentCatalog.ItemsFileName),
            Shipped(ContentCatalog.EnemiesFileName),
            Shipped(ContentCatalog.LootFileName),
            Shipped(ContentCatalog.SpawnsFileName),
            Shipped(ContentCatalog.NpcsFileName),
            skillsJson);

    private static string Skills(string skill, string bar = "") =>
        "{\"version\":1,\"skills\":[" + skill + "],\"defaultQuickbar\":[" + bar + "]}";

    private const string Cleave =
        "{\"key\":\"skill.cleave\",\"displayName\":\"Cleave\",\"description\":\"d\",\"iconKey\":\"skill.cleave\",\"kind\":\"MeleeArc\",\"manaCost\":8,\"cooldownTicks\":96,\"range\":3.5,\"arcDegrees\":120,\"maxTargets\":4,\"damagePercent\":80}";

    [Fact]
    public void TheShippedSkills_AreCleaveBoltAndMend_WithTheSpecifiedNumbers()
    {
        ContentCatalog catalog = ContentCatalog.Shipped;

        Assert.Equal(["skill.cleave", "skill.bolt", "skill.mend"], catalog.Skills.Select(s => s.Key).ToArray());
        Assert.True(catalog.TryGetSkill("SKILL.CLEAVE", out SkillDefinition cleave));
        Assert.Equal(new SkillDefinition(
            "skill.cleave", "Cleave", "A wide swing that hits everything in front of you.", "skill.cleave",
            SkillKind.MeleeArc, 8, 96, 3.5f, 120f, 4, 80, 0), cleave);
        Assert.True(catalog.TryGetSkill("skill.bolt", out SkillDefinition bolt));
        Assert.Equal((SkillKind.RangedBolt, 10, 64, 18f, 1, 120), (bolt.Kind, bolt.ManaCost, bolt.CooldownTicks, bolt.Range, bolt.MaxTargets, bolt.DamagePercent));
        Assert.True(catalog.TryGetSkill("skill.mend", out SkillDefinition mend));
        Assert.Equal((SkillKind.SelfHeal, 15, 320, 35), (mend.Kind, mend.ManaCost, mend.CooldownTicks, mend.HealAmount));
        Assert.False(catalog.TryGetSkill("skill.nope", out _));
    }

    [Fact]
    public void TheShippedDefaultQuickbar_HasSixSlots_InOrder()
    {
        Assert.Equal(
            [
                new QuickbarSlotSnapshot(0, QuickbarEntryKind.Skill, "skill.cleave"),
                new QuickbarSlotSnapshot(1, QuickbarEntryKind.Skill, "skill.bolt"),
                new QuickbarSlotSnapshot(2, QuickbarEntryKind.Skill, "skill.mend"),
                new QuickbarSlotSnapshot(3, QuickbarEntryKind.None, string.Empty),
                new QuickbarSlotSnapshot(4, QuickbarEntryKind.Item, "potion.health"),
                new QuickbarSlotSnapshot(5, QuickbarEntryKind.Item, "potion.mana")
            ],
            ContentCatalog.Shipped.DefaultQuickbar);
        Assert.Equal(InventoryConstants.QuickbarSlots, ContentCatalog.Shipped.DefaultQuickbar.Count);
    }

    [Fact]
    public void ThePotions_AreUsableOneByOneItems_TheQuartermasterSells()
    {
        ContentCatalog catalog = ContentCatalog.Shipped;

        Assert.True(catalog.TryGetItem("potion.health", out ItemDefinition health));
        Assert.Equal(new ItemUseDefinition(50, 0, 320), health.Use);
        Assert.Equal(0, health.EquipSlotMask);
        Assert.Empty(health.EquipSlots);
        Assert.Equal(ItemFootprint.One, health.Footprint);
        Assert.Equal(3, health.SellPrice);
        Assert.True(catalog.TryGetItem("potion.mana", out ItemDefinition mana));
        Assert.Equal(new ItemUseDefinition(0, 40, 320), mana.Use);

        NpcDefinition quartermaster = Assert.Single(catalog.Npcs);
        Assert.Contains(quartermaster.Stock, e => e.ItemKind == "potion.health" && e.Price == 12);
        Assert.Contains(quartermaster.Stock, e => e.ItemKind == "potion.mana" && e.Price == 12);
        Assert.Null(catalog.Items.First(i => i.Key == "tool.torch").Use);
    }

    [Fact]
    public void WithoutASkillsFile_ThereAreNoSkills_AndTheBarIsSixEmptySlots()
    {
        ContentCatalog catalog = ContentCatalog.Load(
            Shipped(ContentCatalog.ItemsFileName), Shipped(ContentCatalog.EnemiesFileName), Shipped(ContentCatalog.LootFileName));

        Assert.Empty(catalog.Skills);
        Assert.Equal(InventoryConstants.QuickbarSlots, catalog.DefaultQuickbar.Count);
        Assert.All(catalog.DefaultQuickbar, entry => Assert.Equal(QuickbarEntryKind.None, entry.Kind));
    }

    [Fact]
    public void TheDigest_CoversSkillsAndTheDefaultBar()
    {
        string none = LoadWith(Skills(string.Empty)).ContentDigest;
        string withSkill = LoadWith(Skills(Cleave)).ContentDigest;
        string withBar = LoadWith(Skills(Cleave, "{\"kind\":\"Skill\",\"key\":\"skill.cleave\"}")).ContentDigest;
        string tweaked = LoadWith(Skills(Cleave.Replace("\"manaCost\":8", "\"manaCost\":9", StringComparison.Ordinal))).ContentDigest;

        Assert.Equal(4, new[] { none, withSkill, withBar, tweaked }.Distinct().Count());
        Assert.Equal(withSkill, LoadWith(Skills(Cleave)).ContentDigest);
    }

    [Theory]
    [InlineData("\"manaCost\":8", "\"manaCost\":1001")]
    [InlineData("\"manaCost\":8", "\"manaCost\":-1")]
    [InlineData("\"cooldownTicks\":96", "\"cooldownTicks\":64001")]
    [InlineData("\"range\":3.5", "\"range\":64.5")]
    [InlineData("\"arcDegrees\":120", "\"arcDegrees\":361")]
    [InlineData("\"maxTargets\":4", "\"maxTargets\":0")]
    [InlineData("\"maxTargets\":4", "\"maxTargets\":17")]
    [InlineData("\"damagePercent\":80", "\"damagePercent\":1001")]
    [InlineData("\"damagePercent\":80", "\"damagePercent\":0")]
    [InlineData("\"kind\":\"MeleeArc\"", "\"kind\":\"Meteor\"")]
    [InlineData("\"kind\":\"MeleeArc\"", "\"kind\":\"SelfHeal\"")]
    [InlineData("\"key\":\"skill.cleave\"", "\"key\":\"\"")]
    public void ASkillOutsideItsLimits_IsRefused(string from, string to)
    {
        Assert.Contains(from, Cleave, StringComparison.Ordinal);

        Assert.Throws<ContentValidationException>(() => LoadWith(Skills(Cleave.Replace(from, to, StringComparison.Ordinal))));
    }

    [Fact]
    public void AHealAbove10000_AndADuplicateKey_AreRefused()
    {
        string mend = "{\"key\":\"skill.mend\",\"displayName\":\"Mend\",\"description\":\"d\",\"iconKey\":\"skill.mend\",\"kind\":\"SelfHeal\",\"manaCost\":1,\"cooldownTicks\":1,\"healAmount\":10000}";

        Assert.NotNull(LoadWith(Skills(mend)));
        Assert.Throws<ContentValidationException>(() => LoadWith(Skills(mend.Replace("10000", "10001", StringComparison.Ordinal))));
        Assert.Throws<ContentValidationException>(() => LoadWith(Skills(mend + "," + mend.Replace("skill.mend", "SKILL.MEND", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("{\"kind\":\"Skill\",\"key\":\"skill.ghost\"}")]
    [InlineData("{\"kind\":\"Item\",\"key\":\"tool.torch\"}")]
    [InlineData("{\"kind\":\"Item\",\"key\":\"potion.ghost\"}")]
    [InlineData("{\"kind\":\"None\",\"key\":\"skill.cleave\"}")]
    [InlineData("{\"kind\":\"Wand\"}")]
    [InlineData("{\"kind\":\"Skill\"}")]
    public void ADefaultBarEntryThatDoesNotResolve_IsRefused(string entry)
    {
        Assert.Throws<ContentValidationException>(() => LoadWith(Skills(Cleave, entry)));
    }

    [Fact]
    public void ADefaultBarOfMoreThanSixEntries_IsRefused_AndAShortOneIsPaddedWithNone()
    {
        string entry = "{\"kind\":\"Skill\",\"key\":\"skill.cleave\"}";

        Assert.Throws<ContentValidationException>(() => LoadWith(Skills(Cleave, string.Join(",", Enumerable.Repeat(entry, 7)))));
        ContentCatalog padded = LoadWith(Skills(Cleave, entry));
        Assert.Equal(InventoryConstants.QuickbarSlots, padded.DefaultQuickbar.Count);
        Assert.Equal(QuickbarEntryKind.Skill, padded.DefaultQuickbar[0].Kind);
        Assert.Equal(QuickbarEntryKind.None, padded.DefaultQuickbar[5].Kind);
    }

    [Fact]
    public void AnItemWithoutEquipmentSlots_NeedsAUseObject_AndTheUseObjectIsChecked()
    {
        string items = Shipped(ContentCatalog.ItemsFileName);
        string healthUse = "\"use\": { \"restoreHealth\": 50, \"restoreMana\": 0, \"cooldownTicks\": 320 }";
        string manaUse = "\"use\": { \"restoreHealth\": 0, \"restoreMana\": 40, \"cooldownTicks\": 320 }";
        Assert.Contains(healthUse, items, StringComparison.Ordinal);
        Assert.Contains(manaUse, items, StringComparison.Ordinal);
        string skills = Shipped(ContentCatalog.SkillsFileName);

        string noUse = items.Replace(healthUse, "\"footprint\": [1, 1]", StringComparison.Ordinal);
        string tooMuch = items.Replace("\"restoreHealth\": 50", "\"restoreHealth\": 10001", StringComparison.Ordinal);
        string nothing = items.Replace(manaUse, "\"use\": { \"restoreHealth\": 0, \"restoreMana\": 0, \"cooldownTicks\": 320 }", StringComparison.Ordinal);
        string slowest = items.Replace(manaUse, "\"use\": { \"restoreHealth\": 0, \"restoreMana\": 40, \"cooldownTicks\": 64001 }", StringComparison.Ordinal);

        Assert.Throws<ContentValidationException>(() => LoadWith(skills, noUse));
        Assert.Throws<ContentValidationException>(() => LoadWith(skills, tooMuch));
        Assert.Throws<ContentValidationException>(() => LoadWith(skills, nothing));
        Assert.Throws<ContentValidationException>(() => LoadWith(skills, slowest));
    }
}
