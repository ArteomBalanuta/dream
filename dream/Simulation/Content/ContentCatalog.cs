using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;
using Dream.Simulation.World;

namespace Dream.Simulation.Content;

// The authored definitions (config/content/items.json and enemies.json), loaded and validated once
// and then read-only. Parsing happens at load (server start, or the first read of Shipped), never
// per tick; lookups afterwards allocate nothing. Instances reference definitions by string key, so
// a retired key must be handled by the caller (TryGet returns false), never assumed present.
//
// ContentDigest covers the parsed values, not the file bytes, so whitespace and line endings do not
// change it. It is a lockstep input: two servers with different content diverge.
public sealed partial class ContentCatalog
{
    public const string WolfKey = "Wolf";

    public const string ItemsFileName = "items.json";
    public const string EnemiesFileName = "enemies.json";
    public const string LootFileName = "loot.json";
    public const string SpawnsFileName = "spawns.json";
    public const string NpcsFileName = "npcs.json";
    public const string SkillsFileName = "skills.json";

    // The visual an enemy without an explicit key draws (the wolf). The wire leaves it out.
    public const string DefaultEnemyVisualKey = "enemy.wolf";

    public const int MaxCamps = 64;
    public const int MaxCampCount = 8;
    public const float MaxCampCoordinate = 100_000f;
    public const float MaxCampRespawnSeconds = 86_400f;

    // Vendors and gold (M3).
    public const int MaxNpcs = 64;
    public const int MaxNpcStock = 32;
    public const int MaxNpcGreetingLength = 400;
    public const float MaxNpcOffset = 1000f;
    public const int MaxGoldAmount = 1_000_000;

    // Skills and consumables (M4).
    public const int MaxSkills = 64;
    public const int MaxManaCost = 1000;
    public const int MaxCooldownTicks = 64_000;
    public const float MaxSkillRange = 64f;
    public const int MaxSkillTargets = 16;
    public const int MaxDamagePercent = 1000;
    public const int MaxHealAmount = 10_000;
    public const int MaxItemRestore = 10_000;

    public const int MaxLootDrops = 4;
    public const int MaxEnemyLevel = 1000;

    // The largest authored maxHealth of an enemy template.
    public const int MaxEnemyMaxHealth = 100_000;
    public const int MaxLootWeight = 1_000_000;
    public const int MaxLootEntries = 64;

    // Modifier and roll values stay inside ItemRollCodec.TryGetValueRange, the limits a stored roll must meet too.

    private static readonly Lazy<ContentCatalog> ShippedCatalog = new(LoadShipped, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, ItemDefinition> _items;
    private readonly Dictionary<string, EnemyTemplate> _enemies;
    private readonly Dictionary<string, LootTable> _lootTables;
    private readonly Dictionary<string, SkillDefinition> _skills;

    private ContentCatalog(
        Dictionary<string, ItemDefinition> items,
        IReadOnlyList<ItemDefinition> itemList,
        IReadOnlyList<StarterKitEntry> starterKit,
        int starterKitVersion,
        int unrecordedStarterKitVersion,
        Dictionary<string, EnemyTemplate> enemies,
        IReadOnlyList<EnemyTemplate> enemyList,
        Dictionary<string, LootTable> lootTables,
        IReadOnlyList<LootTable> lootList,
        IReadOnlyList<SpawnCamp> camps,
        IReadOnlyList<NpcDefinition> npcs,
        Dictionary<string, SkillDefinition> skills,
        IReadOnlyList<SkillDefinition> skillList,
        IReadOnlyList<QuickbarSlotSnapshot> defaultQuickbar,
        string digest)
    {
        _items = items;
        Items = itemList;
        StarterKit = starterKit;
        StarterKitVersion = starterKitVersion;
        UnrecordedStarterKitVersion = unrecordedStarterKitVersion;
        _enemies = enemies;
        Enemies = enemyList;
        _lootTables = lootTables;
        LootTables = lootList;
        Camps = camps;
        Npcs = npcs;
        _skills = skills;
        Skills = skillList;
        DefaultQuickbar = defaultQuickbar;
        ContentDigest = digest;
    }

    // The catalog the game ships with, from config/content next to the executable. Throws
    // ContentValidationException (rethrown as is on every read) when a file is missing or invalid;
    // the server start path should read it first so that surfaces as a startup failure.
    public static ContentCatalog Shipped => ShippedCatalog.Value;

    public IReadOnlyList<ItemDefinition> Items { get; }

    public IReadOnlyList<EnemyTemplate> Enemies { get; }

    public IReadOnlyList<LootTable> LootTables { get; }

    // The spawn camps in file order (spawns.json). Empty when the content has none, and the runtime
    // then falls back to the world-wide wolf population step.
    public IReadOnlyList<SpawnCamp> Camps { get; }

    // The vendors in file order (npcs.json). Empty when the content has none.
    public IReadOnlyList<NpcDefinition> Npcs { get; }

    // The skills in file order (skills.json). Empty when the content has none.
    public IReadOnlyList<SkillDefinition> Skills { get; }

    // What a character that never bound its quickbar shows: always InventoryConstants.QuickbarSlots
    // entries, slots 0..5 in order, None where the file lists nothing.
    public IReadOnlyList<QuickbarSlotSnapshot> DefaultQuickbar { get; }

    public bool TryGetSkill(string key, out SkillDefinition skill)
    {
        if (key != null && _skills.TryGetValue(key, out SkillDefinition? found))
        {
            skill = found;
            return true;
        }

        skill = null!;
        return false;
    }

    // The starter kit in seeding order, with the kit version that introduced each kind.
    public IReadOnlyList<StarterKitEntry> StarterKit { get; }

    public int StarterKitVersion { get; }

    public int UnrecordedStarterKitVersion { get; }

    // Lowercase hex SHA-256 of the canonical content.
    public string ContentDigest { get; }

    public bool TryGetItem(string key, out ItemDefinition definition)
    {
        if (key != null && _items.TryGetValue(key, out ItemDefinition? found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }

    public bool TryGetEnemy(string key, out EnemyTemplate template)
    {
        if (key != null && _enemies.TryGetValue(key, out EnemyTemplate? found))
        {
            template = found;
            return true;
        }

        template = null!;
        return false;
    }

    public bool TryGetLootTable(string key, out LootTable table)
    {
        if (key != null && _lootTables.TryGetValue(key, out LootTable? found))
        {
            table = found;
            return true;
        }

        table = null!;
        return false;
    }

    // The wolf template, which Load guarantees exists.
    public EnemyTemplate Wolf => _enemies[WolfKey];

    public static string ShippedDirectory => Path.Combine(AppContext.BaseDirectory, "config", "content");

    public static ContentCatalog LoadFromDirectory(string directory)
    {
        string itemsPath = Path.Combine(directory, ItemsFileName);
        string enemiesPath = Path.Combine(directory, EnemiesFileName);
        string itemsJson = ReadFile(itemsPath);
        string enemiesJson = ReadFile(enemiesPath);
        string lootJson = ReadFile(Path.Combine(directory, LootFileName));
        // spawns.json is optional: a content directory without it has no camps.
        string spawnsPath = Path.Combine(directory, SpawnsFileName);
        string spawnsJson = File.Exists(spawnsPath) ? ReadFile(spawnsPath) : EmptySpawnsJson;
        // npcs.json is optional too: a content directory without it has no vendors.
        string npcsPath = Path.Combine(directory, NpcsFileName);
        string npcsJson = File.Exists(npcsPath) ? ReadFile(npcsPath) : EmptyNpcsJson;
        // skills.json is optional as well: a content directory without it has no skills.
        string skillsPath = Path.Combine(directory, SkillsFileName);
        string skillsJson = File.Exists(skillsPath) ? ReadFile(skillsPath) : EmptySkillsJson;
        // quests.json is optional as well: a content directory without it has no quests.
        string questsPath = Path.Combine(directory, QuestsFileName);
        string questsJson = File.Exists(questsPath) ? ReadFile(questsPath) : EmptyQuestsJson;
        return Load(itemsJson, enemiesJson, lootJson, spawnsJson, npcsJson, skillsJson, questsJson);
    }

    // A catalog with no loot tables (fixtures that predate loot.json).
    public static ContentCatalog Load(string itemsJson, string enemiesJson) =>
        Load(itemsJson, enemiesJson, EmptyLootJson);

    private const string EmptyLootJson = "{\"version\":1,\"tables\":[]}";

    private const string EmptySpawnsJson = "{\"version\":1,\"camps\":[]}";

    public static ContentCatalog Load(string itemsJson, string enemiesJson, string lootJson) =>
        Load(itemsJson, enemiesJson, lootJson, EmptySpawnsJson);

    private const string EmptyNpcsJson = "{\"version\":1,\"npcs\":[]}";

    public static ContentCatalog Load(string itemsJson, string enemiesJson, string lootJson, string spawnsJson) =>
        Load(itemsJson, enemiesJson, lootJson, spawnsJson, EmptyNpcsJson);

    private const string EmptySkillsJson = "{\"version\":1,\"skills\":[],\"defaultQuickbar\":[]}";

    public static ContentCatalog Load(string itemsJson, string enemiesJson, string lootJson, string spawnsJson, string npcsJson) =>
        Load(itemsJson, enemiesJson, lootJson, spawnsJson, npcsJson, EmptySkillsJson);

    public static ContentCatalog Load(string itemsJson, string enemiesJson, string lootJson, string spawnsJson, string npcsJson, string skillsJson) =>
        Load(itemsJson, enemiesJson, lootJson, spawnsJson, npcsJson, skillsJson, EmptyQuestsJson);

    public static ContentCatalog Load(string itemsJson, string enemiesJson, string lootJson, string spawnsJson, string npcsJson, string skillsJson, string questsJson)
    {
        ArgumentNullException.ThrowIfNull(questsJson);
        ArgumentNullException.ThrowIfNull(skillsJson);
        ArgumentNullException.ThrowIfNull(npcsJson);
        ArgumentNullException.ThrowIfNull(spawnsJson);
        ArgumentNullException.ThrowIfNull(itemsJson);
        ArgumentNullException.ThrowIfNull(enemiesJson);
        ArgumentNullException.ThrowIfNull(lootJson);

        ParsedItems items = ParseItems(itemsJson);
        ParsedLoot loot = ParseLoot(lootJson, items);
        ParsedEnemies enemies = ParseEnemies(enemiesJson, loot);
        List<SpawnCamp> camps = ParseSpawns(spawnsJson, enemies);
        List<NpcDefinition> npcs = ParseNpcs(npcsJson, items);
        ParsedSkills skills = ParseSkills(skillsJson, items);
        List<QuestDefinition> quests = ParseQuests(questsJson, items, enemies, npcs);
        string digest = ComputeDigest(items, enemies, loot, camps, npcs, skills, quests);
        ContentCatalog catalog = new(
            items.ByKey,
            items.List,
            items.StarterKit,
            items.StarterKitVersion,
            items.UnrecordedStarterKitVersion,
            enemies.ByKey,
            enemies.List,
            loot.ByKey,
            loot.List,
            camps,
            npcs,
            skills.ByKey,
            skills.List,
            skills.DefaultQuickbar,
            digest);
        catalog.SetQuests(quests);
        return catalog;
    }

    private static ContentCatalog LoadShipped() => LoadFromDirectory(ShippedDirectory);

    private static string ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ContentValidationException($"Cannot read content file '{path}': {exception.Message}", exception);
        }
    }

    // ---- items ----

    private sealed record ParsedItems(
        Dictionary<string, ItemDefinition> ByKey,
        List<ItemDefinition> List,
        List<StarterKitEntry> StarterKit,
        int StarterKitVersion,
        int UnrecordedStarterKitVersion);

    private static ParsedItems ParseItems(string json)
    {
        using JsonDocument document = ParseDocument(ItemsFileName, json);
        JsonElement root = document.RootElement;
        ExpectObject(ItemsFileName, "$", root);
        Dictionary<string, JsonElement> properties = ReadProperties(
            ItemsFileName,
            "$",
            root,
            ["version", "starterKitVersion", "unrecordedStarterKitVersion", "starterKit", "items"]);

        _ = ReadInt(ItemsFileName, "$.version", properties["version"], 2, 2);
        int starterKitVersion = ReadInt(ItemsFileName, "$.starterKitVersion", properties["starterKitVersion"], 1, 1000);
        int unrecordedVersion = ReadInt(ItemsFileName, "$.unrecordedStarterKitVersion", properties["unrecordedStarterKitVersion"], 0, starterKitVersion);

        Dictionary<string, ItemDefinition> byKey = new(StringComparer.OrdinalIgnoreCase);
        List<ItemDefinition> list = [];
        JsonElement itemsElement = properties["items"];
        ExpectArray(ItemsFileName, "$.items", itemsElement);
        int index = 0;
        foreach (JsonElement itemElement in itemsElement.EnumerateArray())
        {
            string path = $"$.items[{index}]";
            ItemDefinition item = ParseItem(path, itemElement);
            if (!byKey.TryAdd(item.Key, item))
            {
                throw Fail(ItemsFileName, $"{path}.key", $"duplicate item key '{item.Key}'");
            }

            list.Add(item);
            index++;
        }

        List<StarterKitEntry> kit = [];
        JsonElement kitElement = properties["starterKit"];
        ExpectArray(ItemsFileName, "$.starterKit", kitElement);
        HashSet<string> kitKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<EquipmentSlot> kitWells = [];
        int highest = 0;
        index = 0;
        foreach (JsonElement entryElement in kitElement.EnumerateArray())
        {
            string path = $"$.starterKit[{index}]";
            ExpectObject(ItemsFileName, path, entryElement);
            Dictionary<string, JsonElement> entry = ReadProperties(ItemsFileName, path, entryElement, ["item", "version"], optional: ["equip"]);
            string itemKey = ReadString(ItemsFileName, $"{path}.item", entry["item"]);
            int entryVersion = ReadInt(ItemsFileName, $"{path}.version", entry["version"], 1, starterKitVersion);
            if (!byKey.TryGetValue(itemKey, out ItemDefinition? referenced))
            {
                throw Fail(ItemsFileName, $"{path}.item", $"starter kit references missing item '{itemKey}'");
            }

            if (!kitKeys.Add(itemKey))
            {
                throw Fail(ItemsFileName, $"{path}.item", $"duplicate starter kit item '{itemKey}'");
            }

            // Optional: the equipment well a brand-new character wears this kind in (see
            // PlayerInventoryService.EquipStarterKitWells). The item must list that slot itself, and
            // two kinds must not claim the same well.
            EquipmentSlot? equipWell = null;
            if (entry.TryGetValue("equip", out JsonElement equipElement))
            {
                EquipmentSlot well = ParseEquipSlotName($"{path}.equip", equipElement);
                if ((referenced.EquipSlotMask & EquipmentSlots.MaskOf(well)) == 0)
                {
                    throw Fail(ItemsFileName, $"{path}.equip", $"item '{itemKey}' cannot be equipped in {well}");
                }

                if (!kitWells.Add(well))
                {
                    throw Fail(ItemsFileName, $"{path}.equip", $"two starter kit items claim the {well} slot");
                }

                equipWell = well;
            }

            // Versions never go down along the kit: an existing entry's version must not change when
            // a newer kind is appended.
            if (entryVersion < highest)
            {
                throw Fail(ItemsFileName, $"{path}.version", $"starter kit versions must not decrease (item '{itemKey}')");
            }

            highest = entryVersion;
            kit.Add(new StarterKitEntry(referenced.Key, entryVersion, equipWell));
            index++;
        }

        if (kit.Count > 0 && highest != starterKitVersion)
        {
            throw Fail(ItemsFileName, "$.starterKitVersion", $"must equal the highest starter kit entry version {highest}, found {starterKitVersion}");
        }

        return new ParsedItems(byKey, list, kit, starterKitVersion, unrecordedVersion);
    }

    private static ItemDefinition ParseItem(string path, JsonElement element)
    {
        ExpectObject(ItemsFileName, path, element);
        Dictionary<string, JsonElement> properties = ReadProperties(
            ItemsFileName,
            path,
            element,
            ["key", "displayName", "description", "visualId", "detailLines", "heldLight", "modifiers", "equipSlots"],
            optional: ["rolls", "footprint", "sellPrice", "use"]);
        string key = ReadKey(ItemsFileName, $"{path}.key", properties["key"]);
        string displayName = ReadName(ItemsFileName, $"{path}.displayName", properties["displayName"]);
        string description = ReadText(ItemsFileName, $"{path}.description", properties["description"], MaxDescriptionLength);
        string visualId = ReadVisualId(ItemsFileName, $"{path}.visualId", properties["visualId"]);

        List<string> detailLines = [];
        JsonElement detailsElement = properties["detailLines"];
        ExpectArray(ItemsFileName, $"{path}.detailLines", detailsElement);
        int lineIndex = 0;
        foreach (JsonElement line in detailsElement.EnumerateArray())
        {
            if (lineIndex >= MaxDetailLines)
            {
                throw Fail(ItemsFileName, $"{path}.detailLines", $"has more than {MaxDetailLines} lines");
            }

            detailLines.Add(ReadText(ItemsFileName, $"{path}.detailLines[{lineIndex}]", line, MaxDetailLineLength));
            lineIndex++;
        }

        string heldLightName = ReadString(ItemsFileName, $"{path}.heldLight", properties["heldLight"]);
        if (!Enum.TryParse(heldLightName, ignoreCase: false, out HeldLightKind heldLight)
            || !Enum.IsDefined(heldLight)
            || !string.Equals(heldLight.ToString(), heldLightName, StringComparison.Ordinal))
        {
            throw Fail(ItemsFileName, $"{path}.heldLight", $"unknown held light '{heldLightName}'");
        }

        List<StatModifier> modifiers = ParseModifiers(path, key, properties["modifiers"]);
        ItemUseDefinition? use = properties.TryGetValue("use", out JsonElement useElement)
            ? ParseItemUse($"{path}.use", useElement)
            : null;
        (int equipSlotMask, List<EquipmentSlot> equipSlots) = ParseEquipSlots($"{path}.equipSlots", properties["equipSlots"], allowEmpty: use != null);
        ItemRollTable? rolls = properties.TryGetValue("rolls", out JsonElement rollsElement)
            ? ParseRolls($"{path}.rolls", rollsElement)
            : null;
        ItemFootprint footprint = properties.TryGetValue("footprint", out JsonElement footprintElement)
            ? ParseFootprint($"{path}.footprint", footprintElement)
            : ItemFootprint.One;

        int sellPrice = properties.TryGetValue("sellPrice", out JsonElement sellElement)
            ? ReadInt(ItemsFileName, $"{path}.sellPrice", sellElement, 0, MaxGoldAmount)
            : 0;

        return new ItemDefinition(
            key,
            displayName,
            description,
            visualId,
            detailLines,
            heldLight,
            modifiers,
            equipSlotMask,
            equipSlots,
            rolls,
            footprint,
            sellPrice,
            use);
    }

    // { "restoreHealth": n, "restoreMana": n, "cooldownTicks": n }; at least one restore above 0.
    private static ItemUseDefinition ParseItemUse(string path, JsonElement element)
    {
        ExpectObject(ItemsFileName, path, element);
        Dictionary<string, JsonElement> use = ReadProperties(ItemsFileName, path, element, ["restoreHealth", "restoreMana", "cooldownTicks"], ["cooldownGroup"]);
        int health = ReadInt(ItemsFileName, $"{path}.restoreHealth", use["restoreHealth"], 0, MaxItemRestore);
        int mana = ReadInt(ItemsFileName, $"{path}.restoreMana", use["restoreMana"], 0, MaxItemRestore);
        int cooldown = ReadInt(ItemsFileName, $"{path}.cooldownTicks", use["cooldownTicks"], 0, MaxCooldownTicks);
        if (health == 0 && mana == 0)
        {
            throw Fail(ItemsFileName, path, "must restore health or mana");
        }

        string? group = use.TryGetValue("cooldownGroup", out JsonElement groupElement)
            ? ReadNonEmptyString(ItemsFileName, $"{path}.cooldownGroup", groupElement)
            : null;
        return new ItemUseDefinition(health, mana, cooldown, group);
    }

    // [columns, rows], each a whole number 1..BagGrid.MaxFootprint. The item is placed by its top-left cell.
    private static ItemFootprint ParseFootprint(string path, JsonElement element)
    {
        ExpectArray(ItemsFileName, path, element);
        if (element.GetArrayLength() != 2)
        {
            throw Fail(ItemsFileName, path, "must be [columns, rows]");
        }

        int cols = ReadInt(ItemsFileName, $"{path}[0]", element[0], 1, BagGrid.MaxFootprint);
        int rows = ReadInt(ItemsFileName, $"{path}[1]", element[1], 1, BagGrid.MaxFootprint);
        return new ItemFootprint((byte)cols, (byte)rows);
    }

    // Only the live stats may be modified, Flat only, each at most once, at most StatSheet.MaxModifiersPerSource.
    private static List<StatModifier> ParseModifiers(string path, string itemKey, JsonElement modifiersElement)
    {
        List<StatModifier> modifiers = [];
        uint seen = 0;
        ExpectArray(ItemsFileName, $"{path}.modifiers", modifiersElement);
        if (modifiersElement.GetArrayLength() > StatSheet.MaxModifiersPerSource)
        {
            throw Fail(ItemsFileName, $"{path}.modifiers", $"has more than {StatSheet.MaxModifiersPerSource} modifiers");
        }

        int modifierIndex = 0;
        foreach (JsonElement modifierElement in modifiersElement.EnumerateArray())
        {
            string modifierPath = $"{path}.modifiers[{modifierIndex}]";
            ExpectObject(ItemsFileName, modifierPath, modifierElement);
            Dictionary<string, JsonElement> modifier = ReadProperties(ItemsFileName, modifierPath, modifierElement, ["stat", "op", "value"]);
            StatId stat = ReadLiveStat($"{modifierPath}.stat", modifier["stat"]);

            string op = ReadString(ItemsFileName, $"{modifierPath}.op", modifier["op"]);
            if (!string.Equals(op, "Flat", StringComparison.Ordinal))
            {
                throw Fail(ItemsFileName, $"{modifierPath}.op", $"unsupported modifier op '{op}' (only 'Flat')");
            }

            if ((seen & (1u << (int)stat)) != 0)
            {
                throw Fail(ItemsFileName, modifierPath, $"item '{itemKey}' modifies {stat} twice");
            }

            seen |= 1u << (int)stat;
            modifiers.Add(new StatModifier(stat, StatOp.Flat, ReadStatValue($"{modifierPath}.value", modifier["value"], stat)));
            modifierIndex++;
        }

        return modifiers;
    }

    private static StatId ReadLiveStat(string path, JsonElement element)
    {
        string statName = ReadString(ItemsFileName, path, element);
        switch (statName)
        {
            case "Strength":
                return StatId.Strength;
            case "AttackSpeed":
                return StatId.AttackSpeed;
            case "Vitality":
                return StatId.Vitality;
            case "Armor":
                return StatId.Armor;
            case "Agility":
                return StatId.Agility;
            case "Energy":
                return StatId.Energy;
            case "MovementSpeed":
                throw Fail(ItemsFileName, path, $"stat '{statName}' has no effect yet and cannot be modified");
            default:
                throw Fail(ItemsFileName, path, $"unknown stat '{Truncate(statName)}'");
        }
    }

    // Strength, Agility, Vitality, Energy and Armor are whole numbers; Attack Speed is a float.
    private static float ReadStatValue(string path, JsonElement element, StatId stat)
    {
        ItemRollCodec.TryGetValueRange(stat, out float min, out float max);
        return stat == StatId.AttackSpeed
            ? ReadFloat(ItemsFileName, path, element, min, max)
            : ReadInt(ItemsFileName, path, element, (int)min, (int)max);
    }

    private static (int Mask, List<EquipmentSlot> Slots) ParseEquipSlots(string path, JsonElement element, bool allowEmpty = false)
    {
        ExpectArray(ItemsFileName, path, element);
        List<EquipmentSlot> slots = [];
        int mask = 0;
        int index = 0;
        foreach (JsonElement slotElement in element.EnumerateArray())
        {
            string slotPath = $"{path}[{index}]";
            EquipmentSlot slot = ParseEquipSlotName(slotPath, slotElement);
            if ((mask & EquipmentSlots.MaskOf(slot)) != 0)
            {
                throw Fail(ItemsFileName, slotPath, $"duplicate equipment slot '{slot}'");
            }

            mask |= EquipmentSlots.MaskOf(slot);
            slots.Add(slot);
            index++;
        }

        if (slots.Count == 0 && !allowEmpty)
        {
            throw Fail(ItemsFileName, path, "must list at least one equipment slot");
        }

        return (mask, slots);
    }

    // One equipment slot name, exactly as the enum spells it. Shared by an item's equipSlots and a
    // starter-kit entry's equip well.
    private static EquipmentSlot ParseEquipSlotName(string path, JsonElement element)
    {
        string name = ReadString(ItemsFileName, path, element);
        return name switch
        {
            "MainHand" => EquipmentSlot.MainHand,
            "OffHand" => EquipmentSlot.OffHand,
            "Head" => EquipmentSlot.Head,
            "Body" => EquipmentSlot.Body,
            "Hands" => EquipmentSlot.Hands,
            "Feet" => EquipmentSlot.Feet,
            "Legs" => EquipmentSlot.Legs,
            "Neck" => EquipmentSlot.Neck,
            "RingLeft" => EquipmentSlot.RingLeft,
            "RingRight" => EquipmentSlot.RingRight,
            "Back" => EquipmentSlot.Back,
            "Companion" => EquipmentSlot.Companion,
            _ => throw Fail(ItemsFileName, path, $"unknown equipment slot '{Truncate(name)}'"),
        };
    }

    private static ItemRollTable ParseRolls(string path, JsonElement element)
    {
        ExpectObject(ItemsFileName, path, element);
        Dictionary<string, JsonElement> rarities = ReadProperties(ItemsFileName, path, element, ["common", "uncommon", "rare"]);
        return new ItemRollTable(
            ParseRollRanges($"{path}.common", rarities["common"]),
            ParseRollRanges($"{path}.uncommon", rarities["uncommon"]),
            ParseRollRanges($"{path}.rare", rarities["rare"]));
    }

    private static List<ItemRollRange> ParseRollRanges(string path, JsonElement element)
    {
        ExpectArray(ItemsFileName, path, element);
        if (element.GetArrayLength() > StatSheet.MaxModifiersPerSource)
        {
            throw Fail(ItemsFileName, path, $"has more than {StatSheet.MaxModifiersPerSource} ranges");
        }

        List<ItemRollRange> ranges = [];
        uint seen = 0;
        int index = 0;
        foreach (JsonElement rangeElement in element.EnumerateArray())
        {
            string rangePath = $"{path}[{index}]";
            ExpectObject(ItemsFileName, rangePath, rangeElement);
            Dictionary<string, JsonElement> range = ReadProperties(ItemsFileName, rangePath, rangeElement, ["stat", "min", "max"]);
            StatId stat = ReadLiveStat($"{rangePath}.stat", range["stat"]);
            if ((seen & (1u << (int)stat)) != 0)
            {
                throw Fail(ItemsFileName, rangePath, $"rolls {stat} twice");
            }

            seen |= 1u << (int)stat;
            float min = ReadStatValue($"{rangePath}.min", range["min"], stat);
            float max = ReadStatValue($"{rangePath}.max", range["max"], stat);
            if (min > max)
            {
                throw Fail(ItemsFileName, rangePath, $"min {min.ToString(CultureInfo.InvariantCulture)} is above max {max.ToString(CultureInfo.InvariantCulture)}");
            }

            ranges.Add(new ItemRollRange(stat, min, max));
            index++;
        }

        return ranges;
    }

    // ---- enemies ----

    private sealed record ParsedEnemies(Dictionary<string, EnemyTemplate> ByKey, List<EnemyTemplate> List);

    private static ParsedEnemies ParseEnemies(string json, ParsedLoot loot)
    {
        using JsonDocument document = ParseDocument(EnemiesFileName, json);
        JsonElement root = document.RootElement;
        ExpectObject(EnemiesFileName, "$", root);
        Dictionary<string, JsonElement> properties = ReadProperties(EnemiesFileName, "$", root, ["version", "enemies"]);
        _ = ReadInt(EnemiesFileName, "$.version", properties["version"], 2, 2);

        Dictionary<string, EnemyTemplate> byKey = new(StringComparer.OrdinalIgnoreCase);
        List<EnemyTemplate> list = [];
        JsonElement enemiesElement = properties["enemies"];
        ExpectArray(EnemiesFileName, "$.enemies", enemiesElement);
        int index = 0;
        foreach (JsonElement enemyElement in enemiesElement.EnumerateArray())
        {
            string path = $"$.enemies[{index}]";
            ExpectObject(EnemiesFileName, path, enemyElement);
            Dictionary<string, JsonElement> enemy = ReadProperties(
                EnemiesFileName,
                path,
                enemyElement,
                ["key", "displayName", "level", "strength", "vitality", "attackSpeed", "aggroRange", "attackRange", "moveSpeed", "visualKey", "spawnRole"],
                optional: ["attackIntervalSeconds", "lootTable", "goldMin", "goldMax", "maxHealth"]);
            float? interval = enemy.TryGetValue("attackIntervalSeconds", out JsonElement intervalElement)
                ? ReadFloat(EnemiesFileName, $"{path}.attackIntervalSeconds", intervalElement, 0.05f, 3600f)
                : null;
            string? lootTable = null;
            if (enemy.TryGetValue("lootTable", out JsonElement lootElement))
            {
                lootTable = ReadKey(EnemiesFileName, $"{path}.lootTable", lootElement);
                if (!loot.ByKey.ContainsKey(lootTable))
                {
                    throw Fail(EnemiesFileName, $"{path}.lootTable", $"references missing loot table '{Truncate(lootTable)}'");
                }
            }

            int goldMin = enemy.TryGetValue("goldMin", out JsonElement goldMinElement)
                ? ReadInt(EnemiesFileName, $"{path}.goldMin", goldMinElement, 0, MaxGoldAmount)
                : 0;
            int goldMax = enemy.TryGetValue("goldMax", out JsonElement goldMaxElement)
                ? ReadInt(EnemiesFileName, $"{path}.goldMax", goldMaxElement, 0, MaxGoldAmount)
                : goldMin;
            if (goldMax < goldMin)
            {
                throw Fail(EnemiesFileName, $"{path}.goldMax", $"{goldMax} is below goldMin {goldMin}");
            }

            int? maxHealth = enemy.TryGetValue("maxHealth", out JsonElement maxHealthElement)
                ? ReadInt(EnemiesFileName, $"{path}.maxHealth", maxHealthElement, 1, MaxEnemyMaxHealth)
                : null;

            // The wolf stops at 90% of its attack range at most. A range at or below the separation distance
            // (0.95 m) over that fraction would stop it inside the separation, where it shoves the player and
            // never settles within bite range; reject it instead of shipping an enemy that cannot bite.
            float minimumAttackRange = WolfDefinition.PlayerSeparationDistance / EnemyActor.StandOffRangeFraction;
            float attackRange = ReadFloat(EnemiesFileName, $"{path}.attackRange", enemy["attackRange"], 0.1f, 1000f);
            if (attackRange <= minimumAttackRange)
            {
                throw Fail(
                    EnemiesFileName,
                    $"{path}.attackRange",
                    $"{attackRange.ToString(CultureInfo.InvariantCulture)} must exceed {minimumAttackRange.ToString("0.###", CultureInfo.InvariantCulture)} (the player separation distance {WolfDefinition.PlayerSeparationDistance.ToString(CultureInfo.InvariantCulture)} divided by the stand-off fraction {EnemyActor.StandOffRangeFraction.ToString(CultureInfo.InvariantCulture)})");
            }

            EnemyTemplate template = new(
                ReadKey(EnemiesFileName, $"{path}.key", enemy["key"]),
                ReadName(EnemiesFileName, $"{path}.displayName", enemy["displayName"]),
                ReadInt(EnemiesFileName, $"{path}.level", enemy["level"], 1, MaxEnemyLevel),
                ReadInt(EnemiesFileName, $"{path}.strength", enemy["strength"], PlayerCombatStats.MinStrength, PlayerCombatStats.MaxStrength),
                ReadInt(EnemiesFileName, $"{path}.vitality", enemy["vitality"], PlayerCombatStats.MinVitality, PlayerCombatStats.MaxVitality),
                ReadFloat(EnemiesFileName, $"{path}.attackSpeed", enemy["attackSpeed"], PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MaxAttackSpeed),
                interval,
                ReadFloat(EnemiesFileName, $"{path}.aggroRange", enemy["aggroRange"], 0.1f, 1000f),
                attackRange,
                ReadFloat(EnemiesFileName, $"{path}.moveSpeed", enemy["moveSpeed"], MinMoveSpeed, 100f),
                ReadVisualId(EnemiesFileName, $"{path}.visualKey", enemy["visualKey"]),
                lootTable,
                ReadSpawnRole($"{path}.spawnRole", enemy["spawnRole"]),
                goldMin,
                goldMax,
                maxHealth);
            if (!byKey.TryAdd(template.Key, template))
            {
                throw Fail(EnemiesFileName, $"{path}.key", $"duplicate enemy key '{template.Key}'");
            }

            list.Add(template);
            index++;
        }

        // The persisted enemy_kind of a spawned wolf is this exact text; a differently cased key
        // would find the template but write rows under another spelling.
        if (!byKey.TryGetValue(WolfKey, out EnemyTemplate? wolf) || !string.Equals(wolf.Key, WolfKey, StringComparison.Ordinal))
        {
            throw Fail(EnemiesFileName, "$.enemies", $"required enemy '{WolfKey}' is missing (the population step spawns it; the key is case-sensitive)");
        }

        if (!list.Any(template => template.SpawnRole == EnemySpawnRole.Primary))
        {
            throw Fail(EnemiesFileName, "$.enemies", "needs at least one enemy with spawnRole 'Primary'");
        }

        return new ParsedEnemies(byKey, list);
    }

    private static EnemySpawnRole ReadSpawnRole(string path, JsonElement element)
    {
        string name = ReadString(EnemiesFileName, path, element);
        return name switch
        {
            "Primary" => EnemySpawnRole.Primary,
            "NightExtra" => EnemySpawnRole.NightExtra,
            _ => throw Fail(EnemiesFileName, path, $"unknown spawn role '{Truncate(name)}' (Primary or NightExtra)"),
        };
    }

    // ---- spawn camps ----

    private static List<SpawnCamp> ParseSpawns(string json, ParsedEnemies enemies)
    {
        using JsonDocument document = ParseDocument(SpawnsFileName, json);
        JsonElement root = document.RootElement;
        ExpectObject(SpawnsFileName, "$", root);
        Dictionary<string, JsonElement> properties = ReadProperties(SpawnsFileName, "$", root, ["version", "camps"]);
        _ = ReadInt(SpawnsFileName, "$.version", properties["version"], 1, 1);

        List<SpawnCamp> list = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        JsonElement campsElement = properties["camps"];
        ExpectArray(SpawnsFileName, "$.camps", campsElement);
        int index = 0;
        foreach (JsonElement campElement in campsElement.EnumerateArray())
        {
            string path = $"$.camps[{index}]";
            if (index >= MaxCamps)
            {
                throw Fail(SpawnsFileName, "$.camps", $"more than {MaxCamps} camps");
            }

            ExpectObject(SpawnsFileName, path, campElement);
            Dictionary<string, JsonElement> camp = ReadProperties(
                SpawnsFileName,
                path,
                campElement,
                ["key", "template", "x", "z", "count", "respawnSeconds"],
                optional: ["night", "minWorldSize", "maxWorldSize"]);
            string key = ReadKey(SpawnsFileName, $"{path}.key", camp["key"]);
            string template = ReadKey(SpawnsFileName, $"{path}.template", camp["template"]);
            if (!enemies.ByKey.TryGetValue(template, out EnemyTemplate? known))
            {
                throw Fail(SpawnsFileName, $"{path}.template", $"references missing enemy '{Truncate(template)}'");
            }

            bool night = false;
            if (camp.TryGetValue("night", out JsonElement nightElement))
            {
                if (nightElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw Fail(SpawnsFileName, $"{path}.night", "expected true or false");
                }

                night = nightElement.GetBoolean();
            }

            int minWorldSize = 0;
            int maxWorldSize = 0;
            if (camp.TryGetValue("minWorldSize", out JsonElement minElement))
            {
                minWorldSize = ReadInt(SpawnsFileName, $"{path}.minWorldSize", minElement, WorldDescriptor.MinHorizontalSize, WorldDescriptor.MaxHorizontalSize);
            }

            if (camp.TryGetValue("maxWorldSize", out JsonElement maxElement))
            {
                maxWorldSize = ReadInt(SpawnsFileName, $"{path}.maxWorldSize", maxElement, WorldDescriptor.MinHorizontalSize, WorldDescriptor.MaxHorizontalSize);
                if (maxWorldSize < minWorldSize)
                {
                    throw Fail(SpawnsFileName, $"{path}.maxWorldSize", "is below minWorldSize");
                }
            }

            SpawnCamp parsed = new(
                key,
                known.Key,
                ReadFloat(SpawnsFileName, $"{path}.x", camp["x"], 0f, MaxCampCoordinate),
                ReadFloat(SpawnsFileName, $"{path}.z", camp["z"], 0f, MaxCampCoordinate),
                ReadInt(SpawnsFileName, $"{path}.count", camp["count"], 1, MaxCampCount),
                ReadFloat(SpawnsFileName, $"{path}.respawnSeconds", camp["respawnSeconds"], 0.1f, MaxCampRespawnSeconds),
                night,
                minWorldSize,
                maxWorldSize);
            if (minWorldSize > 0 && (parsed.X >= minWorldSize || parsed.Z >= minWorldSize))
            {
                throw Fail(SpawnsFileName, path, $"an absolute camp (minWorldSize {minWorldSize}) needs x and z inside 0..{minWorldSize}");
            }

            if (!keys.Add(parsed.Key))
            {
                throw Fail(SpawnsFileName, $"{path}.key", $"duplicate camp key '{parsed.Key}'");
            }

            list.Add(parsed);
            index++;
        }

        return list;
    }

    // ---- npcs ----

    private static List<NpcDefinition> ParseNpcs(string json, ParsedItems items)
    {
        using JsonDocument document = ParseDocument(NpcsFileName, json);
        JsonElement root = document.RootElement;
        ExpectObject(NpcsFileName, "$", root);
        Dictionary<string, JsonElement> properties = ReadProperties(NpcsFileName, "$", root, ["version", "npcs"]);
        _ = ReadInt(NpcsFileName, "$.version", properties["version"], 1, 1);

        List<NpcDefinition> list = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        JsonElement npcsElement = properties["npcs"];
        ExpectArray(NpcsFileName, "$.npcs", npcsElement);
        int index = 0;
        foreach (JsonElement npcElement in npcsElement.EnumerateArray())
        {
            string path = $"$.npcs[{index}]";
            if (index >= MaxNpcs)
            {
                throw Fail(NpcsFileName, "$.npcs", $"more than {MaxNpcs} npcs");
            }

            ExpectObject(NpcsFileName, path, npcElement);
            Dictionary<string, JsonElement> npc = ReadProperties(
                NpcsFileName,
                path,
                npcElement,
                ["key", "displayName", "visualKey", "offsetX", "offsetZ", "stock"],
                ["greeting"]);
            string key = ReadKey(NpcsFileName, $"{path}.key", npc["key"]);
            if (!keys.Add(key))
            {
                throw Fail(NpcsFileName, $"{path}.key", $"duplicate npc key '{key}'");
            }

            JsonElement stockElement = npc["stock"];
            ExpectArray(NpcsFileName, $"{path}.stock", stockElement);
            if (stockElement.GetArrayLength() > MaxNpcStock)
            {
                throw Fail(NpcsFileName, $"{path}.stock", $"has more than {MaxNpcStock} entries");
            }

            List<NpcStockEntry> stock = [];
            HashSet<string> stocked = new(StringComparer.OrdinalIgnoreCase);
            int stockIndex = 0;
            foreach (JsonElement entryElement in stockElement.EnumerateArray())
            {
                string entryPath = $"{path}.stock[{stockIndex}]";
                ExpectObject(NpcsFileName, entryPath, entryElement);
                Dictionary<string, JsonElement> entry = ReadProperties(NpcsFileName, entryPath, entryElement, ["item", "price"]);
                string itemKey = ReadKey(NpcsFileName, $"{entryPath}.item", entry["item"]);
                if (!items.ByKey.TryGetValue(itemKey, out ItemDefinition? item))
                {
                    throw Fail(NpcsFileName, $"{entryPath}.item", $"references missing item '{Truncate(itemKey)}'");
                }

                // A vendor sells fixed-stat items only: a rolled item would need a seed per sale.
                if (item.Rolls != null)
                {
                    throw Fail(NpcsFileName, $"{entryPath}.item", $"'{item.Key}' is a rolled item; a vendor sells fixed-stat items only");
                }

                if (!stocked.Add(item.Key))
                {
                    throw Fail(NpcsFileName, $"{entryPath}.item", $"duplicate stock item '{item.Key}'");
                }

                int price = ReadInt(NpcsFileName, $"{entryPath}.price", entry["price"], 1, MaxGoldAmount);
                // The buy price must beat the sell price, or a buy-and-sell loop would mint gold.
                if (price <= item.SellPrice)
                {
                    throw Fail(NpcsFileName, $"{entryPath}.price", $"{price} must exceed the sell price {item.SellPrice} of '{item.Key}'");
                }

                stock.Add(new NpcStockEntry(item.Key, price));
                stockIndex++;
            }

            list.Add(new NpcDefinition(
                key,
                ReadName(NpcsFileName, $"{path}.displayName", npc["displayName"]),
                ReadVisualId(NpcsFileName, $"{path}.visualKey", npc["visualKey"]),
                ReadFloat(NpcsFileName, $"{path}.offsetX", npc["offsetX"], -MaxNpcOffset, MaxNpcOffset),
                ReadFloat(NpcsFileName, $"{path}.offsetZ", npc["offsetZ"], -MaxNpcOffset, MaxNpcOffset),
                stock,
                npc.TryGetValue("greeting", out JsonElement greeting)
                    ? ReadText(NpcsFileName, $"{path}.greeting", greeting, MaxNpcGreetingLength)
                    : string.Empty));
            index++;
        }

        return list;
    }

    // ---- skills ----

    private sealed record ParsedSkills(
        Dictionary<string, SkillDefinition> ByKey,
        List<SkillDefinition> List,
        QuickbarSlotSnapshot[] DefaultQuickbar);

    private static ParsedSkills ParseSkills(string json, ParsedItems items)
    {
        using JsonDocument document = ParseDocument(SkillsFileName, json);
        JsonElement root = document.RootElement;
        ExpectObject(SkillsFileName, "$", root);
        Dictionary<string, JsonElement> properties = ReadProperties(SkillsFileName, "$", root, ["version", "skills", "defaultQuickbar"]);
        _ = ReadInt(SkillsFileName, "$.version", properties["version"], 1, 1);

        Dictionary<string, SkillDefinition> byKey = new(StringComparer.OrdinalIgnoreCase);
        List<SkillDefinition> list = [];
        JsonElement skillsElement = properties["skills"];
        ExpectArray(SkillsFileName, "$.skills", skillsElement);
        int index = 0;
        foreach (JsonElement skillElement in skillsElement.EnumerateArray())
        {
            string path = $"$.skills[{index}]";
            if (index >= MaxSkills)
            {
                throw Fail(SkillsFileName, "$.skills", $"more than {MaxSkills} skills");
            }

            SkillDefinition skill = ParseSkill(path, skillElement);
            if (!byKey.TryAdd(skill.Key, skill))
            {
                throw Fail(SkillsFileName, $"{path}.key", $"duplicate skill key '{skill.Key}'");
            }

            list.Add(skill);
            index++;
        }

        QuickbarSlotSnapshot[] defaults = new QuickbarSlotSnapshot[InventoryConstants.QuickbarSlots];
        for (int slot = 0; slot < defaults.Length; slot++)
        {
            defaults[slot] = new QuickbarSlotSnapshot(slot, QuickbarEntryKind.None, string.Empty);
        }

        JsonElement barElement = properties["defaultQuickbar"];
        ExpectArray(SkillsFileName, "$.defaultQuickbar", barElement);
        if (barElement.GetArrayLength() > InventoryConstants.QuickbarSlots)
        {
            throw Fail(SkillsFileName, "$.defaultQuickbar", $"has more than {InventoryConstants.QuickbarSlots} entries");
        }

        int barIndex = 0;
        foreach (JsonElement entryElement in barElement.EnumerateArray())
        {
            string entryPath = $"$.defaultQuickbar[{barIndex}]";
            ExpectObject(SkillsFileName, entryPath, entryElement);
            Dictionary<string, JsonElement> entry = ReadProperties(SkillsFileName, entryPath, entryElement, ["kind"], optional: ["key"]);
            string kindName = ReadString(SkillsFileName, $"{entryPath}.kind", entry["kind"]);
            string key = entry.TryGetValue("key", out JsonElement keyElement)
                ? ReadKey(SkillsFileName, $"{entryPath}.key", keyElement)
                : string.Empty;
            switch (kindName)
            {
                case "None":
                    if (key.Length != 0)
                    {
                        throw Fail(SkillsFileName, $"{entryPath}.key", "a None entry has no key");
                    }

                    break;
                case "Skill":
                    if (!byKey.TryGetValue(key, out SkillDefinition? skill))
                    {
                        throw Fail(SkillsFileName, $"{entryPath}.key", $"references missing skill '{Truncate(key)}'");
                    }

                    defaults[barIndex] = new QuickbarSlotSnapshot(barIndex, QuickbarEntryKind.Skill, skill.Key);
                    break;
                case "Item":
                    if (!items.ByKey.TryGetValue(key, out ItemDefinition? item) || item.Use == null)
                    {
                        throw Fail(SkillsFileName, $"{entryPath}.key", $"'{Truncate(key)}' is not a usable item");
                    }

                    defaults[barIndex] = new QuickbarSlotSnapshot(barIndex, QuickbarEntryKind.Item, item.Key);
                    break;
                default:
                    throw Fail(SkillsFileName, $"{entryPath}.kind", $"unknown quickbar entry kind '{Truncate(kindName)}' (None, Skill or Item)");
            }

            barIndex++;
        }

        return new ParsedSkills(byKey, list, defaults);
    }

    private static SkillDefinition ParseSkill(string path, JsonElement element)
    {
        ExpectObject(SkillsFileName, path, element);
        Dictionary<string, JsonElement> skill = ReadProperties(
            SkillsFileName,
            path,
            element,
            ["key", "displayName", "description", "iconKey", "kind", "manaCost", "cooldownTicks"],
            optional: ["range", "arcDegrees", "maxTargets", "damagePercent", "healAmount"]);
        string kindName = ReadString(SkillsFileName, $"{path}.kind", skill["kind"]);
        if (!Enum.TryParse(kindName, ignoreCase: false, out SkillKind kind)
            || !Enum.IsDefined(kind)
            || !string.Equals(kind.ToString(), kindName, StringComparison.Ordinal))
        {
            throw Fail(SkillsFileName, $"{path}.kind", $"unknown skill kind '{Truncate(kindName)}' (MeleeArc, RangedBolt or SelfHeal)");
        }

        float range = skill.TryGetValue("range", out JsonElement rangeElement)
            ? ReadFloat(SkillsFileName, $"{path}.range", rangeElement, 0f, MaxSkillRange)
            : 0f;
        float arc = skill.TryGetValue("arcDegrees", out JsonElement arcElement)
            ? ReadFloat(SkillsFileName, $"{path}.arcDegrees", arcElement, 0f, 360f)
            : 0f;
        int maxTargets = skill.TryGetValue("maxTargets", out JsonElement targetsElement)
            ? ReadInt(SkillsFileName, $"{path}.maxTargets", targetsElement, 1, MaxSkillTargets)
            : 1;
        int damagePercent = skill.TryGetValue("damagePercent", out JsonElement percentElement)
            ? ReadInt(SkillsFileName, $"{path}.damagePercent", percentElement, 0, MaxDamagePercent)
            : 0;
        int heal = skill.TryGetValue("healAmount", out JsonElement healElement)
            ? ReadInt(SkillsFileName, $"{path}.healAmount", healElement, 0, MaxHealAmount)
            : 0;

        // A skill must do something its kind promises.
        switch (kind)
        {
            case SkillKind.MeleeArc when range <= 0f || arc <= 0f || damagePercent <= 0:
                throw Fail(SkillsFileName, path, "a MeleeArc skill needs range, arcDegrees and damagePercent above 0");
            case SkillKind.RangedBolt when range <= 0f || damagePercent <= 0:
                throw Fail(SkillsFileName, path, "a RangedBolt skill needs range and damagePercent above 0");
            case SkillKind.SelfHeal when heal <= 0:
                throw Fail(SkillsFileName, path, "a SelfHeal skill needs healAmount above 0");
        }

        return new SkillDefinition(
            ReadKey(SkillsFileName, $"{path}.key", skill["key"]),
            ReadName(SkillsFileName, $"{path}.displayName", skill["displayName"]),
            ReadText(SkillsFileName, $"{path}.description", skill["description"], MaxDescriptionLength),
            ReadVisualId(SkillsFileName, $"{path}.iconKey", skill["iconKey"]),
            kind,
            ReadInt(SkillsFileName, $"{path}.manaCost", skill["manaCost"], 0, MaxManaCost),
            ReadInt(SkillsFileName, $"{path}.cooldownTicks", skill["cooldownTicks"], 0, MaxCooldownTicks),
            range,
            arc,
            maxTargets,
            damagePercent,
            heal);
    }

    // ---- loot ----

    private sealed record ParsedLoot(Dictionary<string, LootTable> ByKey, List<LootTable> List);

    private static ParsedLoot ParseLoot(string json, ParsedItems items)
    {
        using JsonDocument document = ParseDocument(LootFileName, json);
        JsonElement root = document.RootElement;
        ExpectObject(LootFileName, "$", root);
        Dictionary<string, JsonElement> properties = ReadProperties(LootFileName, "$", root, ["version", "tables"]);
        _ = ReadInt(LootFileName, "$.version", properties["version"], 1, 1);

        Dictionary<string, LootTable> byKey = new(StringComparer.OrdinalIgnoreCase);
        List<LootTable> list = [];
        JsonElement tablesElement = properties["tables"];
        ExpectArray(LootFileName, "$.tables", tablesElement);
        int index = 0;
        foreach (JsonElement tableElement in tablesElement.EnumerateArray())
        {
            string path = $"$.tables[{index}]";
            LootTable table = ParseLootTable(path, tableElement, items);
            if (!byKey.TryAdd(table.Key, table))
            {
                throw Fail(LootFileName, $"{path}.key", $"duplicate loot table key '{table.Key}'");
            }

            list.Add(table);
            index++;
        }

        return new ParsedLoot(byKey, list);
    }

    private static LootTable ParseLootTable(string path, JsonElement element, ParsedItems items)
    {
        ExpectObject(LootFileName, path, element);
        Dictionary<string, JsonElement> properties = ReadProperties(
            LootFileName, path, element, ["key", "drops", "nothingWeight", "items", "rarity"]);
        string key = ReadKey(LootFileName, $"{path}.key", properties["key"]);
        int drops = ReadInt(LootFileName, $"{path}.drops", properties["drops"], 1, MaxLootDrops);
        int nothingWeight = ReadInt(LootFileName, $"{path}.nothingWeight", properties["nothingWeight"], 0, MaxLootWeight);

        JsonElement itemsElement = properties["items"];
        ExpectArray(LootFileName, $"{path}.items", itemsElement);
        if (itemsElement.GetArrayLength() > MaxLootEntries)
        {
            throw Fail(LootFileName, $"{path}.items", $"has more than {MaxLootEntries} entries");
        }

        List<LootEntry> entries = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        long itemTotal = 0;
        int entryIndex = 0;
        foreach (JsonElement entryElement in itemsElement.EnumerateArray())
        {
            string entryPath = $"{path}.items[{entryIndex}]";
            ExpectObject(LootFileName, entryPath, entryElement);
            Dictionary<string, JsonElement> entry = ReadProperties(LootFileName, entryPath, entryElement, ["item", "weight"]);
            string itemKey = ReadString(LootFileName, $"{entryPath}.item", entry["item"]);
            if (!items.ByKey.TryGetValue(itemKey, out ItemDefinition? definition))
            {
                throw Fail(LootFileName, $"{entryPath}.item", $"references missing item '{Truncate(itemKey)}'");
            }

            if (definition.Rolls == null)
            {
                throw Fail(LootFileName, $"{entryPath}.item", $"item '{definition.Key}' has no roll table");
            }

            if (!seen.Add(definition.Key))
            {
                throw Fail(LootFileName, $"{entryPath}.item", $"duplicate loot item '{definition.Key}'");
            }

            int weight = ReadInt(LootFileName, $"{entryPath}.weight", entry["weight"], 0, MaxLootWeight);
            itemTotal += weight;
            entries.Add(new LootEntry(definition.Key, weight));
            entryIndex++;
        }

        if (itemTotal <= 0)
        {
            throw Fail(LootFileName, $"{path}.items", "needs at least one item with a positive weight");
        }

        if (nothingWeight + itemTotal > int.MaxValue)
        {
            throw Fail(LootFileName, $"{path}.items", "weights add up past the integer range");
        }

        JsonElement rarityElement = properties["rarity"];
        ExpectObject(LootFileName, $"{path}.rarity", rarityElement);
        Dictionary<string, JsonElement> rarity = ReadProperties(LootFileName, $"{path}.rarity", rarityElement, ["common", "uncommon", "rare"]);
        int common = ReadInt(LootFileName, $"{path}.rarity.common", rarity["common"], 0, MaxLootWeight);
        int uncommon = ReadInt(LootFileName, $"{path}.rarity.uncommon", rarity["uncommon"], 0, MaxLootWeight);
        int rare = ReadInt(LootFileName, $"{path}.rarity.rare", rarity["rare"], 0, MaxLootWeight);
        if ((long)common + uncommon + rare <= 0)
        {
            throw Fail(LootFileName, $"{path}.rarity", "needs at least one positive weight");
        }

        return new LootTable(key, drops, nothingWeight, entries, common, uncommon, rare);
    }

    // ---- digest ----

    private static string ComputeDigest(ParsedItems items, ParsedEnemies enemies, ParsedLoot loot, List<SpawnCamp> camps, List<NpcDefinition> npcs, ParsedSkills skills, List<QuestDefinition> quests)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder text = new();
        // Every free-text field is length-prefixed ("5:Stone"), so a '|' or a newline inside a value
        // cannot shift into the next field and make two different catalogs hash alike.
        text.Append("items.v5;kit=").Append(items.StarterKitVersion.ToString(c))
            .Append(";unrecorded=").Append(items.UnrecordedStarterKitVersion.ToString(c)).Append('\n');
        foreach (StarterKitEntry entry in items.StarterKit)
        {
            text.Append("kit|");
            // The equip well is simulation-visible - it decides what a brand-new character wears -
            // so two catalogs whose items.json differ only in "equip" must not hash alike.
            AppendField(text, entry.ItemKind).Append('|').Append(entry.IntroducedVersion.ToString(c))
                .Append('|').Append(entry.EquipWell?.ToString() ?? "-").Append('\n');
        }

        foreach (ItemDefinition item in items.List.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            text.Append("item|");
            AppendField(text, item.Key).Append('|');
            AppendField(text, item.DisplayName).Append('|');
            AppendField(text, item.Description).Append('|');
            AppendField(text, item.VisualId).Append('|');
            text.Append(item.HeldLight.ToString()).Append('\n');
            foreach (string line in item.DetailLines)
            {
                text.Append("detail|");
                AppendField(text, line).Append('\n');
            }

            foreach (StatModifier modifier in item.Modifiers)
            {
                text.Append("mod|").Append(modifier.Stat.ToString()).Append("|Flat|")
                    .Append(modifier.Value.ToString("R", c)).Append('\n');
            }

            text.Append("slots|");
            foreach (EquipmentSlot slot in item.EquipSlots)
            {
                text.Append(slot.ToString()).Append(',');
            }

            text.Append('\n');
            text.Append("foot|").Append(item.Footprint.Cols.ToString(c)).Append('x').Append(item.Footprint.Rows.ToString(c)).Append('\n');
            if (item.SellPrice > 0)
            {
                text.Append("sell|").Append(item.SellPrice.ToString(c)).Append('\n');
            }

            if (item.Use != null)
            {
                text.Append("use|").Append(item.Use.RestoreHealth.ToString(c)).Append('|')
                    .Append(item.Use.RestoreMana.ToString(c)).Append('|').Append(item.Use.CooldownTicks.ToString(c)).Append('\n');
            }

            if (item.Rolls != null)
            {
                AppendRolls(text, "common", item.Rolls.Common);
                AppendRolls(text, "uncommon", item.Rolls.Uncommon);
                AppendRolls(text, "rare", item.Rolls.Rare);
            }
        }

        text.Append("enemies.v3\n");
        foreach (EnemyTemplate enemy in enemies.List.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            text.Append("enemy|");
            AppendField(text, enemy.Key).Append('|');
            AppendField(text, enemy.DisplayName).Append('|')
                .Append(enemy.Level.ToString(c)).Append('|').Append(enemy.Strength.ToString(c)).Append('|')
                .Append(enemy.Vitality.ToString(c)).Append('|').Append(enemy.AttackSpeed.ToString("R", c)).Append('|')
                .Append(enemy.AttackIntervalSeconds.HasValue ? enemy.AttackIntervalSeconds.Value.ToString("R", c) : "-").Append('|')
                .Append(enemy.AggroRange.ToString("R", c)).Append('|').Append(enemy.AttackRange.ToString("R", c)).Append('|')
                .Append(enemy.MoveSpeed.ToString("R", c)).Append('|');
            AppendField(text, enemy.VisualKey).Append('|');
            AppendField(text, enemy.LootTable ?? "-").Append('|').Append(enemy.SpawnRole.ToString()).Append('\n');
            if (enemy.GoldMax > 0)
            {
                text.Append("gold|").Append(enemy.GoldMin.ToString(c)).Append('|').Append(enemy.GoldMax.ToString(c)).Append('\n');
            }

            // Only when authored, so content without it keeps its digest.
            if (enemy.MaxHealth.HasValue)
            {
                text.Append("maxhealth|").Append(enemy.MaxHealth.Value.ToString(c)).Append('\n');
            }
        }

        text.Append("loot.v1\n");
        foreach (LootTable table in loot.List.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            text.Append("table|");
            AppendField(text, table.Key).Append('|').Append(table.Drops.ToString(c)).Append('|')
                .Append(table.NothingWeight.ToString(c)).Append('|').Append(table.CommonWeight.ToString(c)).Append('|')
                .Append(table.UncommonWeight.ToString(c)).Append('|').Append(table.RareWeight.ToString(c)).Append('\n');
            foreach (LootEntry entry in table.Items)
            {
                text.Append("loot|");
                AppendField(text, entry.ItemKind).Append('|').Append(entry.Weight.ToString(c)).Append('\n');
            }
        }

        // Only a catalog with camps adds a section, so content without camps keeps its digest.
        if (camps.Count > 0)
        {
            text.Append("spawns.v1\n");
            foreach (SpawnCamp camp in camps)
            {
                text.Append("camp|");
                AppendField(text, camp.Key).Append('|');
                AppendField(text, camp.Template).Append('|')
                    .Append(camp.X.ToString("R", c)).Append('|').Append(camp.Z.ToString("R", c)).Append('|')
                    .Append(camp.Count.ToString(c)).Append('|').Append(camp.RespawnSeconds.ToString("R", c)).Append('|')
                    .Append(camp.Night ? "night" : "any");
                if (camp.MinWorldSize > 0 || camp.MaxWorldSize > 0)
                {
                    text.Append("|band|").Append(camp.MinWorldSize.ToString(c)).Append('|').Append(camp.MaxWorldSize.ToString(c));
                }

                text.Append('\n');
            }
        }

        // Likewise for vendors.
        if (npcs.Count > 0)
        {
            text.Append("npcs.v1\n");
            foreach (NpcDefinition npc in npcs)
            {
                text.Append("npc|");
                AppendField(text, npc.Key).Append('|');
                AppendField(text, npc.DisplayName).Append('|');
                AppendField(text, npc.VisualKey).Append('|')
                    .Append(npc.OffsetX.ToString("R", c)).Append('|').Append(npc.OffsetZ.ToString("R", c)).Append('\n');
                if (npc.Greeting.Length > 0)
                {
                    text.Append("greeting|");
                    AppendField(text, npc.Greeting).Append('\n');
                }

                foreach (NpcStockEntry entry in npc.Stock)
                {
                    text.Append("stock|");
                    AppendField(text, entry.ItemKind).Append('|').Append(entry.Price.ToString(c)).Append('\n');
                }
            }
        }

        // Likewise for skills and the default quickbar.
        if (skills.List.Count > 0 || skills.DefaultQuickbar.Any(entry => entry.Kind != QuickbarEntryKind.None))
        {
            text.Append("skills.v1\n");
            foreach (SkillDefinition skill in skills.List)
            {
                text.Append("skill|");
                AppendField(text, skill.Key).Append('|');
                AppendField(text, skill.DisplayName).Append('|');
                AppendField(text, skill.Description).Append('|');
                AppendField(text, skill.IconKey).Append('|')
                    .Append(skill.Kind.ToString()).Append('|').Append(skill.ManaCost.ToString(c)).Append('|')
                    .Append(skill.CooldownTicks.ToString(c)).Append('|').Append(skill.Range.ToString("R", c)).Append('|')
                    .Append(skill.ArcDegrees.ToString("R", c)).Append('|').Append(skill.MaxTargets.ToString(c)).Append('|')
                    .Append(skill.DamagePercent.ToString(c)).Append('|').Append(skill.HealAmount.ToString(c)).Append('\n');
            }

            foreach (QuickbarSlotSnapshot entry in skills.DefaultQuickbar)
            {
                text.Append("bar|").Append(entry.Slot.ToString(c)).Append('|').Append(((int)entry.Kind).ToString(c)).Append('|');
                AppendField(text, entry.Key).Append('\n');
            }
        }

        AppendQuestDigest(text, quests);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static void AppendRolls(StringBuilder text, string rarity, IReadOnlyList<ItemRollRange> ranges)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        foreach (ItemRollRange range in ranges)
        {
            text.Append("roll|").Append(rarity).Append('|').Append(range.Stat.ToString()).Append('|')
                .Append(range.Min.ToString("R", c)).Append('|').Append(range.Max.ToString("R", c)).Append('\n');
        }
    }

    private static StringBuilder AppendField(StringBuilder text, string value) =>
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    // ---- JSON helpers ----

    private static ContentValidationException Fail(string file, string path, string problem) =>
        new($"Invalid content in {file} at {path}: {problem}.");

    private static JsonDocument ParseDocument(string file, string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException exception)
        {
            throw new ContentValidationException($"Invalid content in {file}: not valid JSON ({exception.Message}).", exception);
        }
    }

    private static void ExpectObject(string file, string path, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Fail(file, path, "expected an object");
        }
    }

    private static void ExpectArray(string file, string path, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Fail(file, path, "expected an array");
        }
    }

    // Every property must be known, none repeated, and all required ones present. Optional names are
    // allowed and may be absent.
    private static Dictionary<string, JsonElement> ReadProperties(string file, string path, JsonElement element, string[] allowed, string[]? optional = null)
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0 && (optional == null || Array.IndexOf(optional, property.Name) < 0))
            {
                throw Fail(file, $"{path}.{property.Name}", "unknown property");
            }

            if (!result.TryAdd(property.Name, property.Value))
            {
                throw Fail(file, $"{path}.{property.Name}", "duplicate property");
            }
        }

        foreach (string name in allowed)
        {
            if (!result.ContainsKey(name))
            {
                throw Fail(file, $"{path}.{name}", "missing required property");
            }
        }

        return result;
    }

    private static string ReadString(string file, string path, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Fail(file, path, "expected a string");
        }

        return element.GetString()!;
    }

    private static string ReadNonEmptyString(string file, string path, JsonElement element)
    {
        string value = ReadString(file, path, element);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Fail(file, path, "must not be blank");
        }

        return value;
    }

    // Limits in UTF-16 chars. Names and descriptions travel in inventory snapshots, which have a frame
    // cap, so a content file cannot make one item arbitrarily large.
    public const int MaxKeyLength = 64;
    public const int MaxDisplayNameLength = 64;
    public const int MaxVisualIdLength = 64;
    public const int MaxDescriptionLength = 512;
    public const int MaxDetailLineLength = 256;
    public const int MaxDetailLines = 16;

    // An enemy that cannot move at all (0, -0.0) or moves a hair per second is a content mistake.
    public const float MinMoveSpeed = 0.01f;

    // Free text may hold line breaks and tabs, no other control character (NUL included).
    private static string ReadText(string file, string path, JsonElement element, int maxLength)
    {
        string value = ReadString(file, path, element);
        if (value.Length > maxLength)
        {
            throw Fail(file, path, $"is {value.Length} characters long (at most {maxLength})");
        }

        foreach (char c in value)
        {
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            {
                throw Fail(file, path, "must not contain control characters");
            }
        }

        return value;
    }

    private static string ReadName(string file, string path, JsonElement element)
    {
        string value = ReadText(file, path, element, MaxDisplayNameLength);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Fail(file, path, "must not be blank");
        }

        if (value.AsSpan().IndexOfAny('\n', '\r', '\t') >= 0)
        {
            throw Fail(file, path, "must be a single line");
        }

        return value;
    }

    // Lower-case dotted ids such as "held.tool.blade".
    private static string ReadVisualId(string file, string path, JsonElement element)
    {
        string value = ReadString(file, path, element);
        if (value.Length == 0 || value.Length > MaxVisualIdLength || !IsVisualId(value))
        {
            throw Fail(file, path, $"'{Truncate(value)}' is not a visual id (1..{MaxVisualIdLength} characters of a-z, 0-9, '.', '-', '_', starting and ending with a letter or digit)");
        }

        return value;
    }

    private static bool IsVisualId(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool alnum = c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            bool joiner = c is '.' or '-' or '_';
            if (!alnum && !(joiner && i > 0 && i < value.Length - 1))
            {
                return false;
            }
        }

        return true;
    }

    private static string Truncate(string value) =>
        (value.Length > 32 ? value[..32] + "..." : value).Replace("\0", "\\0", StringComparison.Ordinal);

    // A content key is looked up by value (case-insensitively), so surrounding whitespace would make a
    // key that no lookup finds.
    private static string ReadKey(string file, string path, JsonElement element)
    {
        string value = ReadNonEmptyString(file, path, element);
        if (value.Length > MaxKeyLength)
        {
            throw Fail(file, path, $"is {value.Length} characters long (a key is at most {MaxKeyLength})");
        }

        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                throw Fail(file, path, "must not contain control characters");
            }
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Fail(file, path, "must not have leading or trailing whitespace");
        }

        return value;
    }

    private static int ReadInt(string file, string path, JsonElement element, int min, int max)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
        {
            throw Fail(file, path, "expected an integer");
        }

        if (value < min || value > max)
        {
            throw Fail(file, path, $"{value} is outside the range {min}..{max}");
        }

        return value;
    }

    private static float ReadFloat(string file, string path, JsonElement element, float min, float max)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out float value) || !float.IsFinite(value))
        {
            throw Fail(file, path, "expected a finite number");
        }

        if (value < min || value > max)
        {
            throw Fail(file, path, $"{value.ToString(CultureInfo.InvariantCulture)} is outside the range {min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}");
        }

        return value;
    }
}
