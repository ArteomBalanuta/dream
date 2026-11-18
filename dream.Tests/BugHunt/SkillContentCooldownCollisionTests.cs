using System.Text.Json.Nodes;
using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SKILL-3: skill keys and item cooldown groups share one AbilityCooldowns list, compared
// ordinal-ignore-case, and nothing compared the two namespaces at load. An author who named a skill
// "potion.health" (the default group of every health potion) shipped a bar where drinking a potion
// greyed the skill and casting the skill blocked the potion, with no load error and a HUD that agreed
// with the server, so it read as a mystery rather than a content mistake. skills.json now refuses to
// load, the same shape as the per-giver quest cap and the vendor stock cap.
public sealed class SkillContentCooldownCollisionTests
{
    private static string ShippedFile(string file)
        => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, file));

    // The shipped catalog with items.json and/or skills.json replaced.
    private static ContentCatalog Load(string? itemsJson = null, string? skillsJson = null) =>
        ContentCatalog.Load(
            itemsJson ?? ShippedFile(ContentCatalog.ItemsFileName),
            ShippedFile(ContentCatalog.EnemiesFileName),
            ShippedFile(ContentCatalog.LootFileName),
            ShippedFile(ContentCatalog.SpawnsFileName),
            ShippedFile(ContentCatalog.NpcsFileName),
            skillsJson ?? ShippedFile(ContentCatalog.SkillsFileName),
            ShippedFile(ContentCatalog.QuestsFileName));

    // The shipped skills.json with one more SelfHeal skill under the given key. It is appended, so the
    // default quickbar still names the three shipped skills and only the new key is ever the reason to fail.
    private static string SkillsPlus(string key)
    {
        JsonNode root = JsonNode.Parse(ShippedFile(ContentCatalog.SkillsFileName))!;
        JsonObject added = new()
        {
            ["key"] = key,
            ["displayName"] = "Extra",
            ["description"] = "An authored skill.",
            ["iconKey"] = "skill.mend",
            ["kind"] = "SelfHeal",
            ["manaCost"] = 5,
            ["cooldownTicks"] = 64,
            ["healAmount"] = 10,
        };
        root["skills"]!.AsArray().Add(added);
        return root.ToJsonString();
    }

    // The shipped items.json with one consumable's explicit use.cooldownGroup set.
    private static string ItemsWithCooldownGroup(string itemKey, string group)
    {
        JsonNode root = JsonNode.Parse(ShippedFile(ContentCatalog.ItemsFileName))!;
        JsonNode item = root["items"]!.AsArray().First(node => (string?)node!["key"] == itemKey)!;
        item["use"]!["cooldownGroup"] = group;
        return root.ToJsonString();
    }

    // Control: nothing shipped collides, so the catalog still loads and keeps all three skills.
    [Fact]
    public void TheShippedContent_StillLoads_AndNoSkillSharesACooldownGroup()
    {
        ContentCatalog shipped = ContentCatalog.Shipped;

        Assert.Equal(3, shipped.Skills.Count);
        foreach (SkillDefinition skill in shipped.Skills)
        {
            Assert.DoesNotContain(
                shipped.Items,
                item => item.Use != null
                    && string.Equals(item.Use.EffectiveCooldownGroup, skill.Key, StringComparison.OrdinalIgnoreCase));
        }
    }

    // The default group of every health potion, taken as a skill key: refused, naming the file, the
    // path and the item it would have collided with.
    [Fact]
    public void ASkillKeyedOnTheDefaultPotionGroup_IsRefusedAtLoad()
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(
            () => Load(skillsJson: SkillsPlus(ItemUseDefinition.HealthGroup)));

        Assert.Contains(ContentCatalog.SkillsFileName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("$.skills[3].key", failure.Message, StringComparison.Ordinal);
        Assert.Contains("cooldown group", failure.Message, StringComparison.Ordinal);
        Assert.Contains("potion.health", failure.Message, StringComparison.Ordinal);
    }

    // The mana half of the same default pair.
    [Fact]
    public void ASkillKeyedOnTheDefaultManaGroup_IsRefusedAtLoad()
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(
            () => Load(skillsJson: SkillsPlus(ItemUseDefinition.ManaGroup)));

        Assert.Contains("cooldown group", failure.Message, StringComparison.Ordinal);
        Assert.Contains("potion.mana", failure.Message, StringComparison.Ordinal);
    }

    // The other direction: an item authored onto a shipped skill's key.
    [Fact]
    public void AnItemCooldownGroupOnAShippedSkillKey_IsRefusedAtLoad()
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(
            () => Load(itemsJson: ItemsWithCooldownGroup("potion.health", "skill.mend")));

        Assert.Contains(ContentCatalog.SkillsFileName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("skill.mend", failure.Message, StringComparison.Ordinal);
        Assert.Contains("potion.health", failure.Message, StringComparison.Ordinal);
    }

    // The keys are compared the way AbilityCooldowns compares them, so case is not an escape.
    [Fact]
    public void ACollisionDifferingOnlyInCase_IsRefusedAtLoad()
    {
        Assert.Throws<ContentValidationException>(
            () => Load(skillsJson: SkillsPlus("Potion.Health")));
    }

    // Control: a skill key that collides with nothing still loads, and so does an authored group of its own.
    [Fact]
    public void ASkillKeyThatCollidesWithNothing_StillLoads()
    {
        ContentCatalog catalog = Load(
            itemsJson: ItemsWithCooldownGroup("potion.health", "potion.restore"),
            skillsJson: SkillsPlus("skill.extra"));

        Assert.Equal(4, catalog.Skills.Count);
        Assert.True(catalog.TryGetSkill("skill.extra", out _));
        Assert.Equal(
            "potion.restore",
            catalog.Items.Single(item => item.Key == "potion.health").Use!.EffectiveCooldownGroup);
    }
}
