using System.IO;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Xunit;

namespace Dream.Tests.Simulation.Content;

// F2: the shipped content equals the values that used to be hard-coded, and the loader rejects
// malformed content with a message that names the file and path.
public sealed class ContentCatalogTests
{
    private static string ShippedItemsJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.ItemsFileName));

    private static string ShippedEnemiesJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.EnemiesFileName));

    [Fact]
    public void ShippedContent_IsCopiedNextToTheExecutable_AndLoads()
    {
        Assert.True(File.Exists(Path.Combine(ContentCatalog.ShippedDirectory, "items.json")));
        Assert.True(File.Exists(Path.Combine(ContentCatalog.ShippedDirectory, "enemies.json")));
        Assert.Equal(5, ContentCatalog.Shipped.Items.Count);
        Assert.Matches("^[0-9a-f]{64}$", ContentCatalog.Shipped.ContentDigest);
    }

    [Fact]
    public void ShippedItems_EqualTheFormerHardCodedTools()
    {
        ContentCatalog catalog = ContentCatalog.Shipped;

        AssertTool(catalog, "tool.builder-hammer", "Builder Hammer",
            "Reliable close-range construction tool for quick world edits.", "held.tool.builder",
            ["Role: Building", "Impact: +2 Strength", "Tempo: +0.25 Attack Speed"],
            new ToolStatModifier(StrengthBonus: 2, AttackSpeedBonus: 0.25f), HeldLightKind.None);
        AssertTool(catalog, "tool.field-blade", "Field Blade",
            "Short combat blade with quick recovery and clean follow-up swings.", "held.tool.blade",
            ["Role: Combat", "Impact: +4 Strength", "Tempo: +1.50 Attack Speed"],
            new ToolStatModifier(StrengthBonus: 4, AttackSpeedBonus: 1.50f), HeldLightKind.None);
        AssertTool(catalog, "tool.stone-maul", "Stone Maul",
            "Heavy breaker meant for deliberate hits and strong stagger pressure.", "held.tool.maul",
            ["Role: Breaker", "Impact: +8 Strength", "Tempo: -0.75 Attack Speed"],
            new ToolStatModifier(StrengthBonus: 8, AttackSpeedBonus: -0.75f), HeldLightKind.None);
        AssertTool(catalog, "tool.survey-lantern", "Survey Lantern",
            "Utility tool for traversal loadouts and world inspection passes.", "held.tool.lantern",
            ["Role: Utility", "Impact: +1 Strength", "Tempo: +0.10 Attack Speed"],
            new ToolStatModifier(StrengthBonus: 1, AttackSpeedBonus: 0.10f), HeldLightKind.Lantern);
        AssertTool(catalog, "tool.torch", "Torch",
            "A burning torch that lights the ground around you after dark.", "held.tool.torch",
            ["Role: Light", "Light: warm, radius 8"],
            default, HeldLightKind.Torch);
    }

    private static void AssertTool(
        ContentCatalog catalog,
        string key,
        string displayName,
        string description,
        string visualId,
        string[] detailLines,
        ToolStatModifier modifier,
        HeldLightKind heldLight)
    {
        Assert.True(catalog.TryGetItem(key, out ItemDefinition item), key);
        Assert.Equal(key, item.Key);
        Assert.Equal(displayName, item.DisplayName);
        Assert.Equal(description, item.Description);
        Assert.Equal(visualId, item.VisualId);
        Assert.Equal(detailLines, item.DetailLines);
        Assert.Equal(modifier, item.StatModifier);
        Assert.Equal(heldLight, item.HeldLight);

        // The facade hands out the same values.
        Assert.True(ToolCatalog.TryResolve(key, out ToolDefinition tool));
        Assert.Equal(key, tool.ItemKind);
        Assert.Equal(displayName, tool.DisplayName);
        Assert.Equal(modifier, tool.StatModifier);
        Assert.Equal(heldLight, tool.HeldLight);
        Assert.Equal(detailLines, tool.DetailLines);
    }

    [Fact]
    public void ShippedStarterKit_KeepsItsOrderAndVersions()
    {
        StarterKitEntry[] expected =
        [
            new("tool.builder-hammer", 1),
            new("tool.field-blade", 1),
            new("tool.stone-maul", 1),
            new("tool.survey-lantern", 1),
            new("tool.torch", 2)
        ];

        Assert.Equal(expected, ContentCatalog.Shipped.StarterKit);
        Assert.Equal(expected, ToolCatalog.StarterKit);
        Assert.Equal(expected.Select(entry => entry.ItemKind), ToolCatalog.StarterItemKinds);
        Assert.Equal(2, ContentCatalog.Shipped.StarterKitVersion);
        Assert.Equal(2, ToolCatalog.CurrentStarterKitVersion);
        Assert.Equal(1, ContentCatalog.Shipped.UnrecordedStarterKitVersion);
        Assert.Equal(1, ToolCatalog.UnrecordedStarterKitVersion);
    }

    [Fact]
    public void ShippedWolf_EqualsTheFormerHardCodedNumbers()
    {
        Assert.True(ContentCatalog.Shipped.TryGetEnemy("Wolf", out EnemyTemplate wolf));
        Assert.Equal("Wolf", wolf.Key);
        Assert.Equal("Wolf", wolf.DisplayName);
        Assert.Equal(1, wolf.Level);
        Assert.Equal(100, wolf.MaxHealth);
        Assert.Equal(12, wolf.AttackDamage);
        Assert.Equal(1.05f, wolf.AttackIntervalSeconds);
        Assert.Equal(10f, wolf.AggroRange);
        Assert.Equal(1.35f, wolf.AttackRange);
        Assert.Equal(2.8f, wolf.MoveSpeed);
        Assert.Same(wolf, ContentCatalog.Shipped.Wolf);
        Assert.Single(ContentCatalog.Shipped.Enemies);
    }

    [Fact]
    public void TryGet_IsFalseForAnUnknownKey_AndNeverThrows()
    {
        Assert.False(ContentCatalog.Shipped.TryGetItem("tool.retired", out _));
        Assert.False(ContentCatalog.Shipped.TryGetEnemy("Dragon", out _));
        Assert.False(ToolCatalog.TryResolve("tool.retired", out ToolDefinition definition));
        Assert.Equal(default, definition);
        Assert.True(ToolCatalog.TryResolve("TOOL.TORCH", out ToolDefinition torch));
        Assert.Equal("tool.torch", torch.ItemKind);
    }

    [Fact]
    public void ContentDigest_IsStable_IgnoresFormatting_AndChangesWithValues()
    {
        ContentCatalog first = ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson);
        ContentCatalog second = ContentCatalog.Load(ShippedItemsJson.Replace("\r\n", "\n").Replace("  ", "\t"), ShippedEnemiesJson);
        Assert.Equal(first.ContentDigest, second.ContentDigest);
        Assert.Equal(ContentCatalog.Shipped.ContentDigest, first.ContentDigest);

        ContentCatalog changedEnemy = ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson.Replace("\"attackDamage\": 12", "\"attackDamage\": 13"));
        Assert.NotEqual(first.ContentDigest, changedEnemy.ContentDigest);

        ContentCatalog changedItem = ContentCatalog.Load(ShippedItemsJson.Replace("\"value\": 8", "\"value\": 9"), ShippedEnemiesJson);
        Assert.NotEqual(first.ContentDigest, changedItem.ContentDigest);
    }

    [Theory]
    [InlineData("\"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 8", "\"stat\": \"Luck\", \"op\": \"Flat\", \"value\": 8", "unknown stat 'Luck'")]
    [InlineData("\"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 8", "\"stat\": \"Strength\", \"op\": \"Percent\", \"value\": 8", "unsupported modifier op")]
    [InlineData("\"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 8", "\"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 51", "outside the range")]
    [InlineData("\"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 8", "\"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 1.5", "expected an integer")]
    [InlineData("\"value\": -0.75", "\"value\": -9", "outside the range")]
    [InlineData("\"key\": \"tool.torch\"", "\"key\": \"tool.field-blade\"", "duplicate item key")]
    [InlineData("\"key\": \"tool.torch\"", "\"key\": \"TOOL.FIELD-BLADE\"", "duplicate item key")]
    [InlineData("{ \"item\": \"tool.torch\", \"version\": 2 }", "{ \"item\": \"tool.missing\", \"version\": 2 }", "references missing item 'tool.missing'")]
    [InlineData("{ \"item\": \"tool.torch\", \"version\": 2 }", "{ \"item\": \"tool.field-blade\", \"version\": 2 }", "duplicate starter kit item")]
    [InlineData("\"starterKitVersion\": 2", "\"starterKitVersion\": 3", "must equal the highest starter kit entry version")]
    [InlineData("\"heldLight\": \"Torch\"", "\"heldLight\": \"Sun\"", "unknown held light")]
    [InlineData("\"visualId\": \"held.tool.torch\"", "\"visualId\": \"held.tool.torch\", \"extra\": 1", "unknown property")]
    [InlineData("\"displayName\": \"Torch\"", "\"displayName\": \" \"", "must not be blank")]
    public void Load_RejectsInvalidItems_WithAClearMessage(string find, string replace, string expectedFragment)
    {
        string items = ShippedItemsJson;
        Assert.Contains(find, items);
        ContentValidationException exception = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(items.Replace(find, replace), ShippedEnemiesJson));

        Assert.Contains(expectedFragment, exception.Message);
        Assert.Contains("items.json", exception.Message);
    }

    [Theory]
    [InlineData("\"maxHealth\": 100", "\"maxHealth\": 0", "outside the range")]
    [InlineData("\"attackIntervalSeconds\": 1.05", "\"attackIntervalSeconds\": 0", "outside the range")]
    [InlineData("\"moveSpeed\": 2.8", "\"moveSpeed\": -1", "outside the range")]
    [InlineData("\"moveSpeed\": 2.8", "\"moveSpeed\": \"fast\"", "expected a finite number")]
    [InlineData("\"moveSpeed\": 2.8", "\"moveSpeed\": 2.8, \"speed\": 3", "unknown property")]
    [InlineData("\"level\": 1,", "", "missing required property")]
    [InlineData("\"key\": \"Wolf\"", "\"key\": \"Boar\"", "required enemy 'Wolf' is missing")]
    public void Load_RejectsInvalidEnemies_WithAClearMessage(string find, string replace, string expectedFragment)
    {
        string enemies = ShippedEnemiesJson;
        Assert.Contains(find, enemies);
        ContentValidationException exception = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, enemies.Replace(find, replace)));

        Assert.Contains(expectedFragment, exception.Message);
        Assert.Contains("enemies.json", exception.Message);
    }

    [Fact]
    public void Load_RejectsADuplicateEnemyKey()
    {
        string enemies = ShippedEnemiesJson;
        int start = enemies.IndexOf('{', enemies.IndexOf("\"enemies\"", StringComparison.Ordinal));
        int end = enemies.LastIndexOf('}', enemies.LastIndexOf('}') - 1);
        string wolf = enemies[start..(end + 1)];
        string doubled = enemies.Replace(wolf, wolf + "," + wolf);

        ContentValidationException exception = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, doubled));
        Assert.Contains("duplicate enemy key 'Wolf'", exception.Message);
    }

    [Fact]
    public void Load_RejectsDuplicatePropertiesAndBrokenJson()
    {
        ContentValidationException duplicate = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson.Replace("\"level\": 1,", "\"level\": 1, \"level\": 2,")));
        Assert.Contains("duplicate property", duplicate.Message);

        ContentValidationException broken = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load("{ not json", ShippedEnemiesJson));
        Assert.Contains("not valid JSON", broken.Message);

        Assert.Throws<ContentValidationException>(
            () => ContentCatalog.LoadFromDirectory(Path.Combine(Path.GetTempPath(), "dream-no-such-content-" + Guid.NewGuid().ToString("N"))));
    }
}
