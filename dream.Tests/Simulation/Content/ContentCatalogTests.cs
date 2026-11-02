using System.IO;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;
using Xunit;

namespace Dream.Tests.Simulation.Content;

// F2: the shipped content equals the values that used to be hard-coded, and the loader rejects
// malformed content with a message that names the file and path.
public sealed class ContentCatalogTests
{
    private static string ShippedItemsJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.ItemsFileName));

    private static string ShippedLootJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.LootFileName));

    private static string ShippedEnemiesJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.EnemiesFileName));

    [Fact]
    public void ShippedContent_IsCopiedNextToTheExecutable_AndLoads()
    {
        Assert.True(File.Exists(Path.Combine(ContentCatalog.ShippedDirectory, "items.json")));
        Assert.True(File.Exists(Path.Combine(ContentCatalog.ShippedDirectory, "enemies.json")));
        Assert.Equal(57, ContentCatalog.Shipped.Items.Count);
        Assert.Matches("^[0-9a-f]{64}$", ContentCatalog.Shipped.ContentDigest);
    }

    [Fact]
    public void ShippedItems_EqualTheFormerHardCodedTools()
    {
        ContentCatalog catalog = ContentCatalog.Shipped;

        AssertTool(catalog, "tool.builder-hammer", "Builder Hammer",
            "Reliable close-range construction tool for quick world edits.", "held.tool.builder",
            ["Role: Building", "Impact: +2 Strength", "Tempo: +0.25 Attack Speed"],
            [Flat(StatId.Strength, 2f), Flat(StatId.AttackSpeed, 0.25f)], HeldLightKind.None);
        AssertTool(catalog, "tool.field-blade", "Field Blade",
            "Short combat blade with quick recovery and clean follow-up swings.", "held.tool.blade",
            ["Role: Combat", "Impact: +4 Strength", "Tempo: +1.50 Attack Speed"],
            [Flat(StatId.Strength, 4f), Flat(StatId.AttackSpeed, 1.50f)], HeldLightKind.None);
        AssertTool(catalog, "tool.stone-maul", "Stone Maul",
            "Heavy breaker meant for deliberate hits and strong stagger pressure.", "held.tool.maul",
            ["Role: Breaker", "Impact: +8 Strength", "Tempo: -0.75 Attack Speed"],
            [Flat(StatId.Strength, 8f), Flat(StatId.AttackSpeed, -0.75f)], HeldLightKind.None);
        AssertTool(catalog, "tool.survey-lantern", "Survey Lantern",
            "Utility tool for traversal loadouts and world inspection passes.", "held.tool.lantern",
            ["Role: Utility", "Impact: +1 Strength", "Tempo: +0.10 Attack Speed"],
            [Flat(StatId.Strength, 1f), Flat(StatId.AttackSpeed, 0.10f)], HeldLightKind.Lantern);
        AssertTool(catalog, "tool.torch", "Torch",
            "A burning torch that lights the ground around you after dark.", "held.tool.torch",
            ["Role: Light", "Light: warm, radius 8"],
            [], HeldLightKind.Torch);
    }

    private static void AssertTool(
        ContentCatalog catalog,
        string key,
        string displayName,
        string description,
        string visualId,
        string[] detailLines,
        StatModifier[] modifiers,
        HeldLightKind heldLight)
    {
        Assert.True(catalog.TryGetItem(key, out ItemDefinition item), key);
        Assert.Equal(key, item.Key);
        Assert.Equal(displayName, item.DisplayName);
        Assert.Equal(description, item.Description);
        Assert.Equal(visualId, item.VisualId);
        Assert.Equal(detailLines, item.DetailLines);
        Assert.Equal(modifiers, item.Modifiers);
        Assert.Equal(heldLight, item.HeldLight);

        // The facade hands out the same values.
        Assert.True(ToolCatalog.TryResolve(key, out ToolDefinition tool));
        Assert.Equal(key, tool.ItemKind);
        Assert.Equal(displayName, tool.DisplayName);
        Assert.Equal(modifiers, tool.Modifiers);
        Assert.Equal(heldLight, tool.HeldLight);
        Assert.Equal(detailLines, tool.DetailLines);
    }

    private static StatModifier Flat(StatId stat, float value) => new(stat, StatOp.Flat, value);

    [Fact]
    public void ShippedItems_ListTheirEquipSlots_InFileOrder()
    {
        ContentCatalog catalog = ContentCatalog.Shipped;
        AssertSlots(catalog, "tool.builder-hammer", EquipmentSlot.MainHand);
        AssertSlots(catalog, "tool.field-blade", EquipmentSlot.MainHand);
        AssertSlots(catalog, "tool.stone-maul", EquipmentSlot.MainHand);
        AssertSlots(catalog, "tool.survey-lantern", EquipmentSlot.MainHand, EquipmentSlot.OffHand);
        AssertSlots(catalog, "tool.torch", EquipmentSlot.MainHand, EquipmentSlot.OffHand);
        AssertSlots(catalog, "gear.iron-blade", EquipmentSlot.MainHand);
        AssertSlots(catalog, "gear.ward-lantern", EquipmentSlot.OffHand);
        AssertSlots(catalog, "gear.leather-cap", EquipmentSlot.Head);
        AssertSlots(catalog, "gear.leather-vest", EquipmentSlot.Body);
    }

    private static void AssertSlots(ContentCatalog catalog, string key, params EquipmentSlot[] expected)
    {
        Assert.True(catalog.TryGetItem(key, out ItemDefinition item), key);
        Assert.Equal(expected, item.EquipSlots);
        Assert.Equal(expected.Aggregate(0, (mask, slot) => mask | EquipmentSlots.MaskOf(slot)), item.EquipSlotMask);
    }

    [Fact]
    public void ShippedGear_HasRollTables_AndTheStartersHaveNone()
    {
        ContentCatalog catalog = ContentCatalog.Shipped;
        foreach (string key in new[] { "tool.builder-hammer", "tool.field-blade", "tool.stone-maul", "tool.survey-lantern", "tool.torch" })
        {
            Assert.True(catalog.TryGetItem(key, out ItemDefinition starter));
            Assert.Null(starter.Rolls);
        }

        Assert.True(catalog.TryGetItem("gear.iron-blade", out ItemDefinition blade));
        Assert.Equal(
            [new ItemRollRange(StatId.Strength, 7, 9), new ItemRollRange(StatId.AttackSpeed, 1.5f, 2.0f)],
            blade.Rolls!.Rare);
        Assert.True(catalog.TryGetItem("gear.ward-lantern", out ItemDefinition lantern));
        Assert.Equal(HeldLightKind.Lantern, lantern.HeldLight);
        Assert.Equal([new ItemRollRange(StatId.Armor, 2, 4)], lantern.Rolls!.Uncommon);
        Assert.True(catalog.TryGetItem("gear.leather-vest", out ItemDefinition vest));
        Assert.Equal(
            [new ItemRollRange(StatId.Armor, 2, 4), new ItemRollRange(StatId.Vitality, 1, 2)],
            vest.Rolls!.Common);
        Assert.Empty(vest.Modifiers);
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
            new("tool.torch", 2),
            new("consumable.weak-heal", 3),
            new("consumable.minor-heal", 3)
        ];

        Assert.Equal(expected, ContentCatalog.Shipped.StarterKit);
        Assert.Equal(expected, ToolCatalog.StarterKit);
        Assert.Equal(expected.Select(entry => entry.ItemKind), ToolCatalog.StarterItemKinds);
        Assert.Equal(3, ContentCatalog.Shipped.StarterKitVersion);
        Assert.Equal(3, ToolCatalog.CurrentStarterKitVersion);
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
        Assert.Equal(10, wolf.Strength);
        Assert.Equal(10, wolf.Vitality);
        Assert.Equal(0.952381f, wolf.AttackSpeed);
        Assert.Null(wolf.AttackIntervalSeconds);
        Assert.Equal("enemy.wolf", wolf.VisualKey);
        Assert.Equal("wolf", wolf.LootTable);
        Assert.Equal(EnemySpawnRole.Primary, wolf.SpawnRole);
        Assert.Equal(10f, wolf.AggroRange);
        Assert.Equal(1.35f, wolf.AttackRange);
        Assert.Equal(2.8f, wolf.MoveSpeed);
        Assert.Same(wolf, ContentCatalog.Shipped.Wolf);
        Assert.Equal(["Wolf", "DireWolf"], ContentCatalog.Shipped.Enemies.Select(template => template.Key).ToArray());
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
        ContentCatalog first = ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson, ShippedLootJson);
        ContentCatalog second = ContentCatalog.Load(ShippedItemsJson.Replace("\r\n", "\n").Replace("  ", "\t"), ShippedEnemiesJson, ShippedLootJson);
        Assert.Equal(first.ContentDigest, second.ContentDigest);
        Assert.Equal(ContentCatalog.Shipped.ContentDigest, first.ContentDigest);

        ContentCatalog changedEnemy = ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson.Replace("\"strength\": 10", "\"strength\": 11"), ShippedLootJson);
        Assert.NotEqual(first.ContentDigest, changedEnemy.ContentDigest);

        ContentCatalog changedItem = ContentCatalog.Load(ShippedItemsJson.Replace("\"value\": 8", "\"value\": 9"), ShippedEnemiesJson, ShippedLootJson);
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
    [InlineData("\"starterKitVersion\": 3", "\"starterKitVersion\": 4", "must equal the highest starter kit entry version")]
    [InlineData("\"heldLight\": \"Torch\"", "\"heldLight\": \"Sun\"", "unknown held light")]
    [InlineData("\"visualId\": \"held.tool.torch\"", "\"visualId\": \"held.tool.torch\", \"extra\": 1", "unknown property")]
    [InlineData("\"displayName\": \"Torch\"", "\"displayName\": \" \"", "must not be blank")]
    public void Load_RejectsInvalidItems_WithAClearMessage(string find, string replace, string expectedFragment)
    {
        string items = ShippedItemsJson;
        Assert.Contains(find, items);
        ContentValidationException exception = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(items.Replace(find, replace), ShippedEnemiesJson, ShippedLootJson));

        Assert.Contains(expectedFragment, exception.Message);
        Assert.Contains("items.json", exception.Message);
    }

    [Theory]
    [InlineData("\"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 8", "\"stat\": \"Agility\", \"op\": \"Flat\", \"value\": 51", "outside the range")]
    [InlineData("\"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 8", "\"stat\": \"Energy\", \"op\": \"Flat\", \"value\": 1.5", "expected an integer")]
    [InlineData("\"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 8", "\"stat\": \"MovementSpeed\", \"op\": \"Flat\", \"value\": 8", "cannot be modified")]
    [InlineData("\"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 8", "\"stat\": \"Armor\", \"op\": \"Flat\", \"value\": 101", "outside the range")]
    [InlineData("\"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 8", "\"stat\": \"Armor\", \"op\": \"Flat\", \"value\": -1", "outside the range")]
    [InlineData("\"equipSlots\": [\"OffHand\"]", "\"equipSlots\": [\"Wrist\"]", "unknown equipment slot")]
    [InlineData("\"equipSlots\": [\"OffHand\"]", "\"equipSlots\": [\"OffHand\", \"OffHand\"]", "duplicate equipment slot")]
    [InlineData("\"equipSlots\": [\"OffHand\"]", "\"equipSlots\": []", "at least one equipment slot")]
    [InlineData("\"equipSlots\": [\"OffHand\"],", "", "missing required property")]
    [InlineData("{ \"stat\": \"Armor\", \"min\": 1, \"max\": 2 }", "{ \"stat\": \"Armor\", \"min\": 3, \"max\": 2 }", "is above max")]
    [InlineData("{ \"stat\": \"Armor\", \"min\": 1, \"max\": 2 }", "{ \"stat\": \"Armor\", \"min\": 1, \"max\": 2 }, { \"stat\": \"Armor\", \"min\": 1, \"max\": 2 }", "rolls Armor twice")]
    [InlineData("{ \"stat\": \"Armor\", \"min\": 1, \"max\": 2 }", "{ \"stat\": \"MovementSpeed\", \"min\": 1, \"max\": 2 }", "cannot be modified")]
    [InlineData("{ \"stat\": \"Armor\", \"min\": 1, \"max\": 2 }", "{ \"stat\": \"Armor\", \"min\": 1.5, \"max\": 2 }", "expected an integer")]
    [InlineData("{ \"stat\": \"Armor\", \"min\": 1, \"max\": 2 }", "{ \"stat\": \"Armor\", \"min\": 1, \"max\": 101 }", "outside the range")]
    [InlineData("\"uncommon\": [ { \"stat\": \"Armor\", \"min\": 2, \"max\": 4 } ],", "", "missing required property")]
    [InlineData("\"version\": 2,\n  \"starterKitVersion\"", "\"version\": 1,\n  \"starterKitVersion\"", "outside the range")]
    public void Load_RejectsInvalidSlotsStatsAndRolls_WithAClearMessage(string find, string replace, string expectedFragment)
    {
        string items = ShippedItemsJson.Replace("\r\n", "\n");
        Assert.Contains(find, items);
        ContentValidationException exception = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(items.Replace(find, replace), ShippedEnemiesJson, ShippedLootJson));

        Assert.Contains(expectedFragment, exception.Message);
        Assert.Contains("items.json", exception.Message);
    }

    // A wolf stops at 90% of its attack range at most. A range at or below the player separation distance
    // (0.95 m) over that fraction (1.0556 m) would stop it inside the separation, where it shoves the player
    // and never settles in bite range; the catalog refuses it.
    [Theory]
    [InlineData("0.1")]
    [InlineData("0.95")]
    [InlineData("1.0")]
    [InlineData("1.05")]
    public void Load_RejectsAnAttackRangeTheWolfCouldNeverBiteFrom(string range)
    {
        string enemies = ShippedEnemiesJson.Replace("\"attackRange\": 1.35", "\"attackRange\": " + range);
        Assert.NotEqual(ShippedEnemiesJson, enemies);
        ContentValidationException exception = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, enemies, ShippedLootJson));
        Assert.Contains("enemies.json", exception.Message);
        Assert.Contains("attackRange", exception.Message);
        Assert.Contains("must exceed", exception.Message);
    }

    [Theory]
    [InlineData("1.06")]
    [InlineData("1.2")]
    [InlineData("3")]
    public void Load_AcceptsAnAttackRangeAboveTheSeparationOverTheStandOffFraction(string range)
    {
        string enemies = ShippedEnemiesJson.Replace("\"attackRange\": 1.35", "\"attackRange\": " + range);
        ContentCatalog catalog = ContentCatalog.Load(ShippedItemsJson, enemies, ShippedLootJson);
        Assert.All(catalog.Enemies, template => Assert.Equal(float.Parse(range, System.Globalization.CultureInfo.InvariantCulture), template.AttackRange));
    }

    [Fact]
    public void Load_RejectsMoreThanEightModifiers()
    {
        string items = ShippedItemsJson;
        string nine = string.Join(",", Enumerable.Range(0, 9).Select(_ => "{ \"stat\": \"Strength\", \"op\": \"Flat\", \"value\": 1 }"));
        int at = items.IndexOf("\"heldLight\": \"Torch\",", StringComparison.Ordinal);
        int open = items.IndexOf('[', items.IndexOf("\"modifiers\"", at, StringComparison.Ordinal));
        int close = items.IndexOf(']', open);
        ContentValidationException exception = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(items[..(open + 1)] + nine + items[close..], ShippedEnemiesJson, ShippedLootJson));
        Assert.Contains("more than 8 modifiers", exception.Message);
    }

    [Fact]
    public void ContentDigest_ChangesWithSlotsAndRolls()
    {
        string items = ShippedItemsJson;
        ContentCatalog first = ContentCatalog.Load(items, ShippedEnemiesJson, ShippedLootJson);
        ContentCatalog slots = ContentCatalog.Load(items.Replace("\"equipSlots\": [\"Head\"]", "\"equipSlots\": [\"Body\"]"), ShippedEnemiesJson, ShippedLootJson);
        ContentCatalog rolls = ContentCatalog.Load(items.Replace("\"min\": 7, \"max\": 9", "\"min\": 7, \"max\": 10"), ShippedEnemiesJson, ShippedLootJson);
        Assert.NotEqual(first.ContentDigest, slots.ContentDigest);
        Assert.NotEqual(first.ContentDigest, rolls.ContentDigest);
    }

    [Theory]
    [InlineData("\"strength\": 10", "\"strength\": 0", "outside the range")]
    [InlineData("\"strength\": 10", "\"strength\": 1001", "outside the range")]
    [InlineData("\"vitality\": 10", "\"vitality\": 0", "outside the range")]
    [InlineData("\"vitality\": 10", "\"vitality\": 1001", "outside the range")]
    [InlineData("\"attackSpeed\": 0.952381", "\"attackSpeed\": 0.49", "outside the range")]
    [InlineData("\"attackSpeed\": 0.952381", "\"attackSpeed\": 5.1", "outside the range")]
    [InlineData("\"moveSpeed\": 2.8,", "\"moveSpeed\": 2.8, \"attackIntervalSeconds\": 0,", "outside the range")]
    [InlineData("\"level\": 1,", "\"level\": 0,", "outside the range")]
    [InlineData("\"level\": 1,", "\"level\": 1, \"maxHealth\": 100,", "unknown property")]
    [InlineData("\"visualKey\": \"enemy.wolf\"", "\"visualKey\": \"Enemy Wolf\"", "is not a visual id")]
    [InlineData("\"spawnRole\": \"Primary\"", "\"spawnRole\": \"Boss\"", "unknown spawn role")]
    [InlineData("\"spawnRole\": \"Primary\"", "\"spawnRole\": \"NightExtra\"", "needs at least one enemy with spawnRole 'Primary'")]
    [InlineData("\"lootTable\": \"wolf\"", "\"lootTable\": \"missing\"", "references missing loot table")]
    [InlineData("\"version\": 2", "\"version\": 1", "outside the range")]
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
            () => ContentCatalog.Load(ShippedItemsJson, enemies.Replace(find, replace), ShippedLootJson));

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
            () => ContentCatalog.Load(ShippedItemsJson, doubled, ShippedLootJson));
        Assert.Contains("duplicate enemy key 'Wolf'", exception.Message);
    }

    [Fact]
    public void Load_RejectsDuplicatePropertiesAndBrokenJson()
    {
        ContentValidationException duplicate = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson.Replace("\"level\": 1,", "\"level\": 1, \"level\": 2,"), ShippedLootJson));
        Assert.Contains("duplicate property", duplicate.Message);

        ContentValidationException broken = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load("{ not json", ShippedEnemiesJson, ShippedLootJson));
        Assert.Contains("not valid JSON", broken.Message);

        Assert.Throws<ContentValidationException>(
            () => ContentCatalog.LoadFromDirectory(Path.Combine(Path.GetTempPath(), "dream-no-such-content-" + Guid.NewGuid().ToString("N"))));
    }

    // ---- loot.json (wave I) ----

    [Fact]
    public void ShippedLoot_LoadsTheWolfAndDireWolfTables()
    {
        ContentCatalog catalog = ContentCatalog.Shipped;

        Assert.True(File.Exists(Path.Combine(ContentCatalog.ShippedDirectory, "loot.json")));
        Assert.Equal(2, catalog.LootTables.Count);
        Assert.True(catalog.TryGetLootTable("wolf", out LootTable wolf));
        Assert.Equal(1, wolf.Drops);
        Assert.Equal(75, wolf.NothingWeight);
        Assert.Equal(52, wolf.Items.Count);
        Assert.Equal((85, 13, 2), (wolf.CommonWeight, wolf.UncommonWeight, wolf.RareWeight));
        Assert.True(catalog.TryGetLootTable("dire-wolf", out LootTable dire));
        Assert.Equal(2, dire.Drops);
        Assert.Equal(52, dire.Items.Count);
        // The item catalogue joined the tables with the same "nothing" weights as before, so the chance of
        // a drop is only diluted across more kinds (potions carry the most weight).
        Assert.Equal(75, wolf.NothingWeight);
        Assert.Equal(50, dire.NothingWeight);
        Assert.True(wolf.Items.Sum(item => item.Weight) > 25);
        Assert.True(dire.Items.Sum(item => item.Weight) > 50);
        Assert.False(catalog.TryGetLootTable("missing", out _));
        Assert.False(catalog.TryGetLootTable(null!, out _));
    }

    [Fact]
    public void TheTwoArgumentLoad_HasNoLootTables()
    {
        string noLootReference = System.Text.RegularExpressions.Regex.Replace(ShippedEnemiesJson, "\\s*,\\s*\"lootTable\"\\s*:\\s*\"(?:dire-)?wolf\"", string.Empty);
        Assert.DoesNotContain("lootTable", noLootReference);

        ContentCatalog catalog = ContentCatalog.Load(ShippedItemsJson, noLootReference);

        Assert.Empty(catalog.LootTables);
        Assert.Null(catalog.Wolf.LootTable);
        Assert.False(catalog.TryGetLootTable("wolf", out _));
    }

    [Fact]
    public void ContentDigest_ChangesWithALootWeight_AndIgnoresLootFormatting()
    {
        ContentCatalog first = ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson, ShippedLootJson);
        ContentCatalog reformatted = ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson, ShippedLootJson.Replace("\r\n", "\n").Replace("  ", "\t"));
        ContentCatalog changed = ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson, ShippedLootJson.Replace("\"nothingWeight\": 75", "\"nothingWeight\": 76"));

        Assert.Equal(first.ContentDigest, reformatted.ContentDigest);
        Assert.Equal(ContentCatalog.Shipped.ContentDigest, first.ContentDigest);
        Assert.NotEqual(first.ContentDigest, changed.ContentDigest);
    }

    [Theory]
    [InlineData("\"drops\": 1", "\"drops\": 0", "outside the range")]
    [InlineData("\"drops\": 1", "\"drops\": 5", "outside the range")]
    [InlineData("\"nothingWeight\": 75", "\"nothingWeight\": -1", "outside the range")]
    [InlineData("\"nothingWeight\": 75", "\"nothingWeight\": 1000001", "outside the range")]
    [InlineData("\"item\": \"gear.leather-cap\", \"weight\": 4", "\"item\": \"gear.missing\", \"weight\": 4", "references missing item")]
    [InlineData("\"item\": \"gear.leather-cap\", \"weight\": 4", "\"item\": \"tool.torch\", \"weight\": 4", "has no roll table")]
    [InlineData("\"item\": \"gear.leather-vest\", \"weight\": 3", "\"item\": \"gear.leather-cap\", \"weight\": 3", "duplicate loot item")]
    [InlineData("\"key\": \"dire-wolf\"", "\"key\": \"wolf\"", "duplicate loot table key")]
    [InlineData("\"key\": \"dire-wolf\"", "\"key\": \"WOLF\"", "duplicate loot table key")]
    [InlineData("\"common\": 85, \"uncommon\": 13, \"rare\": 2", "\"common\": 0, \"uncommon\": 0, \"rare\": 0", "needs at least one positive weight")]
    [InlineData("\"common\": 85", "\"common\": 85, \"epic\": 1", "unknown property")]
    [InlineData("\"version\": 1", "\"version\": 2", "outside the range")]
    [InlineData("\"weight\": 4 }", "\"weight\": 4, \"extra\": 1 }", "unknown property")]
    public void Load_RejectsInvalidLoot_WithAClearMessage(string find, string replace, string expectedFragment)
    {
        string loot = ShippedLootJson;
        Assert.Contains(find, loot);

        ContentValidationException exception = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson, loot.Replace(find, replace)));

        Assert.Contains(expectedFragment, exception.Message);
        Assert.Contains("loot.json", exception.Message);
    }

    [Fact]
    public void Load_RejectsATableWithNoPositiveItemWeight_TooManyEntries_AndBrokenJson()
    {
        const string noItems = "{\"version\":1,\"tables\":[{\"key\":\"t\",\"drops\":1,\"nothingWeight\":5,\"items\":[],\"rarity\":{\"common\":1,\"uncommon\":0,\"rare\":0}}]}";
        string allZero = noItems.Replace("\"items\":[]", "\"items\":[{\"item\":\"gear.iron-blade\",\"weight\":0}]");
        string overflow = "{\"version\":1,\"tables\":[{\"key\":\"t\",\"drops\":1,\"nothingWeight\":1000000,\"items\":["
            + string.Join(",", Enumerable.Range(0, 3000).Select(i => "{\"item\":\"gear.iron-blade\",\"weight\":1000000}"))
            + "],\"rarity\":{\"common\":1,\"uncommon\":0,\"rare\":0}}]}";

        Assert.Contains("at least one item with a positive weight", Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson, noItems)).Message);
        Assert.Contains("at least one item with a positive weight", Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson, allZero)).Message);
        Assert.Contains("more than", Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson, overflow)).Message);
        Assert.Contains("not valid JSON", Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson, "{ nope")).Message);
    }

    [Fact]
    public void LoadFromDirectory_NeedsLootJson()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dream-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, ContentCatalog.ItemsFileName), ShippedItemsJson);
            File.WriteAllText(Path.Combine(directory, ContentCatalog.EnemiesFileName), ShippedEnemiesJson);

            ContentValidationException exception = Assert.Throws<ContentValidationException>(() => ContentCatalog.LoadFromDirectory(directory));
            Assert.Contains("loot.json", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
