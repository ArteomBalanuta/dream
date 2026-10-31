using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;

namespace Dream.Simulation.Content;

// The authored definitions (config/content/items.json and enemies.json), loaded and validated once
// and then read-only. Parsing happens at load (server start, or the first read of Shipped), never
// per tick; lookups afterwards allocate nothing. Instances reference definitions by string key, so
// a retired key must be handled by the caller (TryGet returns false), never assumed present.
//
// ContentDigest covers the parsed values, not the file bytes, so whitespace and line endings do not
// change it. It is a lockstep input: two servers with different content diverge.
public sealed class ContentCatalog
{
    public const string WolfKey = "Wolf";

    public const string ItemsFileName = "items.json";
    public const string EnemiesFileName = "enemies.json";

    // Modifier and roll values stay inside ItemRollCodec.TryGetValueRange, the limits a stored roll must meet too.

    private static readonly Lazy<ContentCatalog> ShippedCatalog = new(LoadShipped, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, ItemDefinition> _items;
    private readonly Dictionary<string, EnemyTemplate> _enemies;

    private ContentCatalog(
        Dictionary<string, ItemDefinition> items,
        IReadOnlyList<ItemDefinition> itemList,
        IReadOnlyList<StarterKitEntry> starterKit,
        int starterKitVersion,
        int unrecordedStarterKitVersion,
        Dictionary<string, EnemyTemplate> enemies,
        IReadOnlyList<EnemyTemplate> enemyList,
        string digest)
    {
        _items = items;
        Items = itemList;
        StarterKit = starterKit;
        StarterKitVersion = starterKitVersion;
        UnrecordedStarterKitVersion = unrecordedStarterKitVersion;
        _enemies = enemies;
        Enemies = enemyList;
        ContentDigest = digest;
    }

    // The catalog the game ships with, from config/content next to the executable. Throws
    // ContentValidationException (rethrown as is on every read) when a file is missing or invalid;
    // the server start path should read it first so that surfaces as a startup failure.
    public static ContentCatalog Shipped => ShippedCatalog.Value;

    public IReadOnlyList<ItemDefinition> Items { get; }

    public IReadOnlyList<EnemyTemplate> Enemies { get; }

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

    // The wolf template, which Load guarantees exists.
    public EnemyTemplate Wolf => _enemies[WolfKey];

    public static string ShippedDirectory => Path.Combine(AppContext.BaseDirectory, "config", "content");

    public static ContentCatalog LoadFromDirectory(string directory)
    {
        string itemsPath = Path.Combine(directory, ItemsFileName);
        string enemiesPath = Path.Combine(directory, EnemiesFileName);
        string itemsJson = ReadFile(itemsPath);
        string enemiesJson = ReadFile(enemiesPath);
        return Load(itemsJson, enemiesJson);
    }

    public static ContentCatalog Load(string itemsJson, string enemiesJson)
    {
        ArgumentNullException.ThrowIfNull(itemsJson);
        ArgumentNullException.ThrowIfNull(enemiesJson);

        ParsedItems items = ParseItems(itemsJson);
        ParsedEnemies enemies = ParseEnemies(enemiesJson);
        string digest = ComputeDigest(items, enemies);
        return new ContentCatalog(
            items.ByKey,
            items.List,
            items.StarterKit,
            items.StarterKitVersion,
            items.UnrecordedStarterKitVersion,
            enemies.ByKey,
            enemies.List,
            digest);
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
        int highest = 0;
        index = 0;
        foreach (JsonElement entryElement in kitElement.EnumerateArray())
        {
            string path = $"$.starterKit[{index}]";
            ExpectObject(ItemsFileName, path, entryElement);
            Dictionary<string, JsonElement> entry = ReadProperties(ItemsFileName, path, entryElement, ["item", "version"]);
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

            // Versions never go down along the kit: an existing entry's version must not change when
            // a newer kind is appended.
            if (entryVersion < highest)
            {
                throw Fail(ItemsFileName, $"{path}.version", $"starter kit versions must not decrease (item '{itemKey}')");
            }

            highest = entryVersion;
            kit.Add(new StarterKitEntry(referenced.Key, entryVersion));
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
            optional: ["rolls"]);
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
        (int equipSlotMask, List<EquipmentSlot> equipSlots) = ParseEquipSlots($"{path}.equipSlots", properties["equipSlots"]);
        ItemRollTable? rolls = properties.TryGetValue("rolls", out JsonElement rollsElement)
            ? ParseRolls($"{path}.rolls", rollsElement)
            : null;

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
            rolls);
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
            case "Energy":
            case "MovementSpeed":
                throw Fail(ItemsFileName, path, $"stat '{statName}' has no effect yet and cannot be modified");
            default:
                throw Fail(ItemsFileName, path, $"unknown stat '{Truncate(statName)}'");
        }
    }

    // Strength, Vitality and Armor are whole numbers; Attack Speed is a float.
    private static float ReadStatValue(string path, JsonElement element, StatId stat)
    {
        ItemRollCodec.TryGetValueRange(stat, out float min, out float max);
        return stat == StatId.AttackSpeed
            ? ReadFloat(ItemsFileName, path, element, min, max)
            : ReadInt(ItemsFileName, path, element, (int)min, (int)max);
    }

    private static (int Mask, List<EquipmentSlot> Slots) ParseEquipSlots(string path, JsonElement element)
    {
        ExpectArray(ItemsFileName, path, element);
        List<EquipmentSlot> slots = [];
        int mask = 0;
        int index = 0;
        foreach (JsonElement slotElement in element.EnumerateArray())
        {
            string slotPath = $"{path}[{index}]";
            string name = ReadString(ItemsFileName, slotPath, slotElement);
            EquipmentSlot slot = name switch
            {
                "MainHand" => EquipmentSlot.MainHand,
                "OffHand" => EquipmentSlot.OffHand,
                "Head" => EquipmentSlot.Head,
                "Body" => EquipmentSlot.Body,
                _ => throw Fail(ItemsFileName, slotPath, $"unknown equipment slot '{Truncate(name)}'"),
            };
            if ((mask & EquipmentSlots.MaskOf(slot)) != 0)
            {
                throw Fail(ItemsFileName, slotPath, $"duplicate equipment slot '{name}'");
            }

            mask |= EquipmentSlots.MaskOf(slot);
            slots.Add(slot);
            index++;
        }

        if (slots.Count == 0)
        {
            throw Fail(ItemsFileName, path, "must list at least one equipment slot");
        }

        return (mask, slots);
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

    private static ParsedEnemies ParseEnemies(string json)
    {
        using JsonDocument document = ParseDocument(EnemiesFileName, json);
        JsonElement root = document.RootElement;
        ExpectObject(EnemiesFileName, "$", root);
        Dictionary<string, JsonElement> properties = ReadProperties(EnemiesFileName, "$", root, ["version", "enemies"]);
        _ = ReadInt(EnemiesFileName, "$.version", properties["version"], 1, 1);

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
                ["key", "displayName", "level", "maxHealth", "attackDamage", "attackIntervalSeconds", "aggroRange", "attackRange", "moveSpeed"]);
            EnemyTemplate template = new(
                ReadKey(EnemiesFileName, $"{path}.key", enemy["key"]),
                ReadName(EnemiesFileName, $"{path}.displayName", enemy["displayName"]),
                ReadInt(EnemiesFileName, $"{path}.level", enemy["level"], 1, 1000),
                ReadInt(EnemiesFileName, $"{path}.maxHealth", enemy["maxHealth"], 1, 1_000_000),
                ReadInt(EnemiesFileName, $"{path}.attackDamage", enemy["attackDamage"], 1, 1_000_000),
                ReadFloat(EnemiesFileName, $"{path}.attackIntervalSeconds", enemy["attackIntervalSeconds"], 0.05f, 3600f),
                ReadFloat(EnemiesFileName, $"{path}.aggroRange", enemy["aggroRange"], 0.1f, 1000f),
                ReadFloat(EnemiesFileName, $"{path}.attackRange", enemy["attackRange"], 0.1f, 1000f),
                ReadFloat(EnemiesFileName, $"{path}.moveSpeed", enemy["moveSpeed"], MinMoveSpeed, 100f));
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

        return new ParsedEnemies(byKey, list);
    }

    // ---- digest ----

    private static string ComputeDigest(ParsedItems items, ParsedEnemies enemies)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder text = new();
        // Every free-text field is length-prefixed ("5:Stone"), so a '|' or a newline inside a value
        // cannot shift into the next field and make two different catalogs hash alike.
        text.Append("items.v3;kit=").Append(items.StarterKitVersion.ToString(c))
            .Append(";unrecorded=").Append(items.UnrecordedStarterKitVersion.ToString(c)).Append('\n');
        foreach (StarterKitEntry entry in items.StarterKit)
        {
            text.Append("kit|");
            AppendField(text, entry.ItemKind).Append('|').Append(entry.IntroducedVersion.ToString(c)).Append('\n');
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
            if (item.Rolls != null)
            {
                AppendRolls(text, "common", item.Rolls.Common);
                AppendRolls(text, "uncommon", item.Rolls.Uncommon);
                AppendRolls(text, "rare", item.Rolls.Rare);
            }
        }

        text.Append("enemies.v2\n");
        foreach (EnemyTemplate enemy in enemies.List.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            text.Append("enemy|");
            AppendField(text, enemy.Key).Append('|');
            AppendField(text, enemy.DisplayName).Append('|')
                .Append(enemy.Level.ToString(c)).Append('|').Append(enemy.MaxHealth.ToString(c)).Append('|')
                .Append(enemy.AttackDamage.ToString(c)).Append('|').Append(enemy.AttackIntervalSeconds.ToString("R", c)).Append('|')
                .Append(enemy.AggroRange.ToString("R", c)).Append('|').Append(enemy.AttackRange.ToString("R", c)).Append('|')
                .Append(enemy.MoveSpeed.ToString("R", c)).Append('\n');
        }

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
