using System.Text;
using System.Text.Json;
using Dream.Simulation.Stats;

namespace Dream.Simulation.Inventory;

// The stored form of an ItemRoll (item_instances.rolled_modifiers): compact, order-preserving
// versioned JSON, for example {"v":1,"r":"Rare","m":[["Strength","Flat",6],["AttackSpeed","Flat",1.75]]}.
// Floats are written shortest round-trip, so a roll survives a save and load bit for bit; stat names
// are text, so StatId can be reordered without touching rows.
//
// Row content is hostile on load (a hand edit, a copied database): Parse never throws, and anything
// it does not accept becomes ItemRoll.Damaged, which is inert.
public static class ItemRollCodec
{
    public const int Version = 1;
    public const int MaxTextLength = 1024;

    private const int MaxJsonDepth = 4;

    public static string Serialize(ItemRoll roll)
    {
        ArgumentNullException.ThrowIfNull(roll);

        using MemoryStream stream = new(128);
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("r", RarityName(roll.Rarity));
            writer.WriteStartArray("m");
            foreach (StatModifier modifier in roll.Modifiers)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(StatName(modifier.Stat));
                writer.WriteStringValue(OpName(modifier.Op));
                writer.WriteNumberValue(modifier.Value);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    // Null text gives null (an unrolled item). Malformed text, a wrong version, an unknown rarity,
    // stat or op, a stat that is not live, a non-finite or out-of-range value, a duplicate stat or
    // more than StatSheet.MaxModifiersPerSource modifiers gives Damaged.
    public static ItemRoll? Parse(string? text)
    {
        if (text == null)
        {
            return null;
        }

        try
        {
            return TryParseCore(text, out ItemRoll? roll) ? roll : ItemRoll.Damaged;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            return ItemRoll.Damaged;
        }
    }

    // True when the stat may appear in a modifier of an item or a roll, and the range its value may take.
    public static bool TryGetValueRange(StatId stat, out float min, out float max)
    {
        switch (stat)
        {
            case StatId.Strength:
            case StatId.Vitality:
                min = -50f;
                max = 50f;
                return true;
            case StatId.AttackSpeed:
                min = -5f;
                max = 5f;
                return true;
            case StatId.Armor:
                min = 0f;
                max = 100f;
                return true;
            default:
                min = 0f;
                max = 0f;
                return false;
        }
    }

    private static bool TryParseCore(string text, out ItemRoll? roll)
    {
        roll = null;
        if (text.Length == 0 || text.Length > MaxTextLength)
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxJsonDepth,
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        int propertyCount = 0;
        foreach (JsonProperty _ in root.EnumerateObject())
        {
            propertyCount++;
        }

        if (propertyCount != 3
            || !root.TryGetProperty("v", out JsonElement versionElement)
            || !root.TryGetProperty("r", out JsonElement rarityElement)
            || !root.TryGetProperty("m", out JsonElement modifiersElement))
        {
            return false;
        }

        if (versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out int version)
            || version != Version
            || rarityElement.ValueKind != JsonValueKind.String
            || !TryParseRarity(rarityElement.GetString(), out ItemRarity rarity)
            || modifiersElement.ValueKind != JsonValueKind.Array
            || modifiersElement.GetArrayLength() > StatSheet.MaxModifiersPerSource)
        {
            return false;
        }

        List<StatModifier> modifiers = new(modifiersElement.GetArrayLength());
        uint seenStats = 0;
        foreach (JsonElement entry in modifiersElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() != 3)
            {
                return false;
            }

            JsonElement statElement = entry[0];
            JsonElement opElement = entry[1];
            JsonElement valueElement = entry[2];
            if (statElement.ValueKind != JsonValueKind.String
                || opElement.ValueKind != JsonValueKind.String
                || valueElement.ValueKind != JsonValueKind.Number
                || !TryParseStat(statElement.GetString(), out StatId stat)
                || !string.Equals(opElement.GetString(), "Flat", StringComparison.Ordinal)
                || !valueElement.TryGetSingle(out float value)
                || !float.IsFinite(value)
                || !TryGetValueRange(stat, out float min, out float max)
                || value < min
                || value > max
                || (stat != StatId.AttackSpeed && value != MathF.Floor(value)))
            {
                // Strength, Vitality and Armor are whole numbers, as in content validation.
                return false;
            }

            uint bit = 1u << (int)stat;
            if ((seenStats & bit) != 0)
            {
                return false;
            }

            seenStats |= bit;
            modifiers.Add(new StatModifier(stat, StatOp.Flat, value));
        }

        roll = new ItemRoll(rarity, modifiers);
        return true;
    }

    private static string RarityName(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Common => "Common",
        ItemRarity.Uncommon => "Uncommon",
        ItemRarity.Rare => "Rare",
        _ => throw new ArgumentOutOfRangeException(nameof(rarity), rarity, "Unknown item rarity."),
    };

    private static bool TryParseRarity(string? name, out ItemRarity rarity)
    {
        switch (name)
        {
            case "Common":
                rarity = ItemRarity.Common;
                return true;
            case "Uncommon":
                rarity = ItemRarity.Uncommon;
                return true;
            case "Rare":
                rarity = ItemRarity.Rare;
                return true;
            default:
                rarity = ItemRarity.Common;
                return false;
        }
    }

    private static string StatName(StatId stat) => stat switch
    {
        StatId.Strength => "Strength",
        StatId.AttackSpeed => "AttackSpeed",
        StatId.Vitality => "Vitality",
        StatId.Armor => "Armor",
        _ => throw new ArgumentOutOfRangeException(nameof(stat), stat, "Not a live stat."),
    };

    private static bool TryParseStat(string? name, out StatId stat)
    {
        switch (name)
        {
            case "Strength":
                stat = StatId.Strength;
                return true;
            case "AttackSpeed":
                stat = StatId.AttackSpeed;
                return true;
            case "Vitality":
                stat = StatId.Vitality;
                return true;
            case "Armor":
                stat = StatId.Armor;
                return true;
            default:
                stat = StatId.Strength;
                return false;
        }
    }

    private static string OpName(StatOp op) => op switch
    {
        StatOp.Flat => "Flat",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Only Flat modifiers are stored."),
    };
}
