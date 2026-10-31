using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dream.Simulation.Inventory;

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

    // Strength and Vitality bonuses stay inside the base stat range; attack speed inside its own.
    private const int MaxIntegerBonus = 50;
    private const float MaxAttackSpeedBonus = 5f;

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

        _ = ReadInt(ItemsFileName, "$.version", properties["version"], 1, 1);
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
            ["key", "displayName", "description", "visualId", "detailLines", "heldLight", "modifiers"]);
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

        List<ItemModifierDefinition> modifiers = [];
        int strength = 0;
        float attackSpeed = 0f;
        int vitality = 0;
        HashSet<ContentStat> seen = [];
        JsonElement modifiersElement = properties["modifiers"];
        ExpectArray(ItemsFileName, $"{path}.modifiers", modifiersElement);
        int modifierIndex = 0;
        foreach (JsonElement modifierElement in modifiersElement.EnumerateArray())
        {
            string modifierPath = $"{path}.modifiers[{modifierIndex}]";
            ExpectObject(ItemsFileName, modifierPath, modifierElement);
            Dictionary<string, JsonElement> modifier = ReadProperties(ItemsFileName, modifierPath, modifierElement, ["stat", "op", "value"]);
            string statName = ReadString(ItemsFileName, $"{modifierPath}.stat", modifier["stat"]);
            if (!Enum.TryParse(statName, ignoreCase: false, out ContentStat stat)
                || !string.Equals(stat.ToString(), statName, StringComparison.Ordinal))
            {
                throw Fail(ItemsFileName, $"{modifierPath}.stat", $"unknown stat '{statName}'");
            }

            string op = ReadString(ItemsFileName, $"{modifierPath}.op", modifier["op"]);
            if (!string.Equals(op, "Flat", StringComparison.Ordinal))
            {
                throw Fail(ItemsFileName, $"{modifierPath}.op", $"unsupported modifier op '{op}' (only 'Flat')");
            }

            if (!seen.Add(stat))
            {
                throw Fail(ItemsFileName, modifierPath, $"item '{key}' modifies {stat} twice");
            }

            JsonElement valueElement = modifier["value"];
            if (stat == ContentStat.AttackSpeed)
            {
                float value = ReadFloat(ItemsFileName, $"{modifierPath}.value", valueElement, -MaxAttackSpeedBonus, MaxAttackSpeedBonus);
                attackSpeed = value;
                modifiers.Add(new ItemModifierDefinition(stat, value));
            }
            else
            {
                int value = ReadInt(ItemsFileName, $"{modifierPath}.value", valueElement, -MaxIntegerBonus, MaxIntegerBonus);
                if (stat == ContentStat.Strength)
                {
                    strength = value;
                }
                else
                {
                    vitality = value;
                }

                modifiers.Add(new ItemModifierDefinition(stat, value));
            }

            modifierIndex++;
        }

        return new ItemDefinition(
            key,
            displayName,
            description,
            visualId,
            detailLines,
            heldLight,
            modifiers,
            new ToolStatModifier(strength, attackSpeed, vitality));
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
        text.Append("items.v2;kit=").Append(items.StarterKitVersion.ToString(c))
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

            foreach (ItemModifierDefinition modifier in item.Modifiers)
            {
                text.Append("mod|").Append(modifier.Stat.ToString()).Append("|Flat|")
                    .Append(modifier.Value.ToString("R", c)).Append('\n');
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

    // Every property must be known, none repeated, and all present.
    private static Dictionary<string, JsonElement> ReadProperties(string file, string path, JsonElement element, string[] allowed)
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
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
