using System.Text.Json;
using System.Text.Json.Serialization;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;

namespace Dream.Multiplayer.Protocol;

// Hand-written converter for PlayerInventorySnapshot (protocol v13). An inventory can hold 64 bag
// items and 12 worn ones; the item text (names, descriptions, detail lines) is the same for every
// copy of a kind, so it does not travel. The server writes what is specific to the item and the
// client rehydrates the rest from ContentCatalog.Shipped, so every consumer still sees a complete
// InventoryItemSnapshot.
//
// Wire shape:
//   {"playerName":s,"columns":n,"rows":n,
//    "bag":[[anchor,id,kind,flags,[stat,value,...],stack?,name?,visual?],...],
//    "worn":[[slot,id,kind,flags,[stat,value,...],stack?,name?,visual?],...],"gold":n?}
// - gold (protocol v16) is written only when above 0, so a snapshot of a player without gold is
//   byte-identical to v15's; a reader clamps a negative value to 0.
// - bag lists the items on the board by anchor (0..63), then the held-back items (200..215); an empty
//   cell has no entry. worn lists the equipment slots (100..111) that hold an item, main hand
//   included. A reader makes the main hand an empty slot when it is not listed.
// - flags: bits 0-1 the rarity (0 common, 1 uncommon, 2 rare), bit 2 rolled, bit 3 inert (the kind
//   is not in the catalog, or the item is worn in a slot it may not occupy: no stats, no light).
// - The modifier list is [stat, value, ...] pairs and is only written for a rolled item (at most 8
//   pairs); an unrolled item takes its definition's modifiers. A modifier that is not Flat writes
//   stat + 256 * op as its stat (no shipped item has one).
// - Trailing fields are written only when needed: stack (count, 1 when absent), then name and visual
//   (the item's own, when they differ from the definition's, and always for a kind the catalog does
//   not know). Whatever is written is positional, so name forces stack and visual forces name.
//
// A reader reads any property order, skips unknown properties and entries it cannot place (a slot
// outside the ranges above), and reports a malformed shape, more than MaxEntries entries or more than
// StatSheet.MaxModifiersPerSource modifiers as a JsonException. The catalog is only read, never
// written, and nothing here allocates per entry beyond the snapshot itself.
internal sealed class PlayerInventorySnapshotJsonConverter : JsonConverter<PlayerInventorySnapshot>
{
    // The most entries a reader accepts over the bag and worn lists together. A real inventory has at
    // most 64 + 16 + 12.
    internal const int MaxEntries = 200;

    // Longest kind, name or visual string a reader accepts.
    internal const int MaxTextLength = 256;

    private const int RarityMask = 3;
    private const int RolledFlag = 4;
    private const int InertFlag = 8;
    private const int OpShift = 8;

    private static readonly JsonEncodedText PlayerName = JsonEncodedText.Encode("playerName");
    private static readonly JsonEncodedText Columns = JsonEncodedText.Encode("columns");
    private static readonly JsonEncodedText Rows = JsonEncodedText.Encode("rows");
    private static readonly JsonEncodedText Bag = JsonEncodedText.Encode("bag");
    private static readonly JsonEncodedText Worn = JsonEncodedText.Encode("worn");
    private static readonly JsonEncodedText Gold = JsonEncodedText.Encode("gold");

    public override PlayerInventorySnapshot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(PlayerInventorySnapshot));
        string playerName = string.Empty;
        int columns = 0;
        int rows = 0;
        long gold = 0;
        List<InventorySlotSnapshot> bag = [];
        List<InventorySlotSnapshot> worn = [];
        int entries = 0;
        while (HotPathJson.NextProperty(ref reader, nameof(PlayerInventorySnapshot)))
        {
            if (reader.ValueTextEquals(PlayerName.EncodedUtf8Bytes))
            {
                playerName = HotPathJson.ReadNullableString(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals(Columns.EncodedUtf8Bytes))
            {
                columns = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Rows.EncodedUtf8Bytes))
            {
                rows = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Bag.EncodedUtf8Bytes))
            {
                ReadEntries(ref reader, bag, ref entries, equipment: false);
            }
            else if (reader.ValueTextEquals(Worn.EncodedUtf8Bytes))
            {
                ReadEntries(ref reader, worn, ref entries, equipment: true);
            }
            else if (reader.ValueTextEquals(Gold.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out gold))
                {
                    throw new JsonException($"Expected a 64-bit integer for gold, found {reader.TokenType}.");
                }

                gold = Math.Max(gold, 0L);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        InventorySlotSnapshot mainHand = new(EquipmentSlots.FirstSlotIndex, Item: null);
        List<InventorySlotSnapshot>? equipment = null;
        foreach (InventorySlotSnapshot slot in worn)
        {
            if (slot.SlotIndex == EquipmentSlots.FirstSlotIndex)
            {
                mainHand = slot;
            }
            else
            {
                (equipment ??= []).Add(slot);
            }
        }

        equipment?.Sort(static (a, b) => a.SlotIndex.CompareTo(b.SlotIndex));
        return new PlayerInventorySnapshot(playerName, columns, rows, bag, mainHand, equipment, gold);
    }

    public override void Write(Utf8JsonWriter writer, PlayerInventorySnapshot value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(PlayerName, value.PlayerName);
        writer.WriteNumber(Columns, value.Columns);
        writer.WriteNumber(Rows, value.Rows);

        writer.WritePropertyName(Bag);
        writer.WriteStartArray();
        if (value.Slots != null)
        {
            for (int i = 0; i < value.Slots.Count; i++)
            {
                if (value.Slots[i].Item is InventoryItemSnapshot item)
                {
                    WriteEntry(writer, value.Slots[i].SlotIndex, in item);
                }
            }
        }

        writer.WriteEndArray();

        writer.WritePropertyName(Worn);
        writer.WriteStartArray();
        if (value.EquippedSlot is InventorySlotSnapshot { Item: InventoryItemSnapshot mainHand } main)
        {
            WriteEntry(writer, main.SlotIndex, in mainHand);
        }

        if (value.Equipment != null)
        {
            for (int i = 0; i < value.Equipment.Count; i++)
            {
                if (value.Equipment[i].Item is InventoryItemSnapshot item)
                {
                    WriteEntry(writer, value.Equipment[i].SlotIndex, in item);
                }
            }
        }

        writer.WriteEndArray();
        if (value.Gold > 0)
        {
            writer.WriteNumber(Gold, value.Gold);
        }

        writer.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter writer, int slotIndex, in InventoryItemSnapshot item)
    {
        bool known = ContentCatalog.Shipped.TryGetItem(item.ItemKind, out ItemDefinition? definition);
        bool inert = !known || IsInert(in item, definition!);
        int flags = ((int)item.Rarity & RarityMask) | (item.IsRolled ? RolledFlag : 0) | (inert ? InertFlag : 0);

        writer.WriteStartArray();
        writer.WriteNumberValue(slotIndex);
        writer.WriteNumberValue(item.ItemInstanceId);
        writer.WriteStringValue(item.ItemKind);
        writer.WriteNumberValue(flags);

        writer.WriteStartArray();
        if (item.IsRolled && !inert && item.Modifiers != null)
        {
            int count = Math.Min(item.Modifiers.Count, StatSheet.MaxModifiersPerSource);
            for (int i = 0; i < count; i++)
            {
                StatModifier modifier = item.Modifiers[i];
                writer.WriteNumberValue((int)modifier.Stat | ((int)modifier.Op << OpShift));
                writer.WriteNumberValue(modifier.Value);
            }
        }

        writer.WriteEndArray();

        bool needName = !known || !string.Equals(item.DisplayName, definition!.DisplayName, StringComparison.Ordinal);
        bool needVisual = !known || !string.Equals(item.VisualId, definition!.VisualId, StringComparison.Ordinal);
        if (needVisual || needName || item.StackCount != 1)
        {
            writer.WriteNumberValue(item.StackCount);
        }

        if (needVisual || needName)
        {
            writer.WriteStringValue(item.DisplayName);
        }

        if (needVisual)
        {
            writer.WriteStringValue(item.VisualId);
        }

        writer.WriteEndArray();
    }

    // A known kind is inert when it does not carry what its definition gives: its light is off, or
    // (unrolled) its modifiers are missing. A kind with nothing to give reads the same either way.
    private static bool IsInert(in InventoryItemSnapshot item, ItemDefinition definition)
    {
        if (item.HeldLight != definition.HeldLight)
        {
            return true;
        }

        return !item.IsRolled && (item.Modifiers == null) != (definition.Modifiers.Count == 0);
    }

    private static void ReadEntries(ref Utf8JsonReader reader, List<InventorySlotSnapshot> into, ref int entries, bool equipment)
    {
        HotPathJson.ReadValue(ref reader);
        if (reader.TokenType == JsonTokenType.Null)
        {
            return;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an array of inventory entries, found {reader.TokenType}.");
        }

        while (true)
        {
            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of JSON while reading inventory entries.");
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return;
            }

            if (++entries > MaxEntries)
            {
                throw new JsonException($"An inventory snapshot has more than {MaxEntries} entries.");
            }

            if (ReadEntry(ref reader, equipment) is InventorySlotSnapshot slot)
            {
                into.Add(slot);
            }
        }
    }

    // The reader is on the entry's StartArray. Null for an entry whose slot is outside the ranges its
    // list may use; the entry is still read to its end.
    private static InventorySlotSnapshot? ReadEntry(ref Utf8JsonReader reader, bool equipment)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an inventory entry array, found {reader.TokenType}.");
        }

        int slotIndex = ReadInt32Element(ref reader, "slot");
        long itemInstanceId = ReadInt64Element(ref reader);
        string kind = ReadTextElement(ref reader, "kind") ?? throw new JsonException("An inventory entry has a null kind.");
        int flags = ReadInt32Element(ref reader, "flags");
        StatModifier[] wireModifiers = ReadModifiers(ref reader);

        int stackCount = 1;
        string? name = null;
        string? visual = null;
        int trailing = 0;
        while (true)
        {
            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of JSON while reading an inventory entry.");
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            switch (trailing++)
            {
                case 0:
                    stackCount = ReadInt32Token(ref reader, "stack count");
                    break;
                case 1:
                    name = ReadTextToken(ref reader, "name");
                    break;
                case 2:
                    visual = ReadTextToken(ref reader, "visual");
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        bool placeable = equipment
            ? EquipmentSlots.TryFromSlotIndex(slotIndex, out _)
            : InventoryConstants.IsBagAnchor(slotIndex) || InventoryConstants.IsHeldBack(slotIndex);
        if (!placeable)
        {
            return null;
        }

        return new InventorySlotSnapshot(
            slotIndex,
            Rehydrate(itemInstanceId, kind, flags, wireModifiers, stackCount, name, visual),
            IsEquipped: equipment);
    }

    // Builds the full item from what the wire carried and the shipped catalog.
    internal static InventoryItemSnapshot Rehydrate(
        long itemInstanceId,
        string kind,
        int flags,
        StatModifier[] wireModifiers,
        int stackCount,
        string? name,
        string? visual)
    {
        ItemRarity rarity = (ItemRarity)(flags & RarityMask);
        if (!Enum.IsDefined(rarity))
        {
            rarity = ItemRarity.Common;
        }

        bool rolled = (flags & RolledFlag) != 0;
        if (!ContentCatalog.Shipped.TryGetItem(kind, out ItemDefinition? definition))
        {
            // A kind this build does not know: its own text, no stats, no light.
            return new InventoryItemSnapshot(
                itemInstanceId,
                kind,
                name ?? kind,
                visual ?? string.Empty,
                stackCount,
                ToolCatalog.UnknownItemDescription,
                [],
                Modifiers: null,
                HeldLight: HeldLightKind.None,
                EquipSlots: 0,
                Rarity: rarity,
                IsRolled: rolled);
        }

        bool inert = (flags & InertFlag) != 0;
        IReadOnlyList<StatModifier>? modifiers = null;
        if (!inert)
        {
            IReadOnlyList<StatModifier> effective = rolled ? wireModifiers : definition.Modifiers;
            modifiers = effective.Count == 0 ? null : effective;
        }

        return new InventoryItemSnapshot(
            itemInstanceId,
            kind,
            name ?? definition.DisplayName,
            visual ?? definition.VisualId,
            stackCount,
            definition.Description,
            definition.DetailLines,
            modifiers,
            inert ? HeldLightKind.None : definition.HeldLight,
            definition.EquipSlotMask,
            rarity,
            rolled);
    }

    private static readonly StatModifier[] NoModifiers = [];

    // [stat, value, stat, value, ...] -> modifiers. A pair with an unknown stat or op, or a value that
    // is not finite, is dropped; more than the per-source cap, an odd count or a non-number is an error.
    private static StatModifier[] ReadModifiers(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("An inventory entry is missing its modifier list.");
        }

        List<StatModifier>? modifiers = null;
        int numbers = 0;
        int code = 0;
        while (true)
        {
            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of JSON while reading modifiers.");
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException($"Expected a number in a modifier list, found {reader.TokenType}.");
            }

            if ((numbers & 1) == 0)
            {
                if (numbers / 2 >= StatSheet.MaxModifiersPerSource)
                {
                    throw new JsonException($"An inventory entry has more than {StatSheet.MaxModifiersPerSource} modifiers.");
                }

                if (!reader.TryGetInt32(out code))
                {
                    throw new JsonException("A modifier stat must be a 32-bit integer.");
                }
            }
            else
            {
                if (!reader.TryGetSingle(out float value))
                {
                    throw new JsonException("A modifier value must be a single-precision number.");
                }

                int stat = code & ((1 << OpShift) - 1);
                int op = code >> OpShift;
                if (code >= 0 && stat < (int)StatId.Count && op <= (int)StatOp.Percent && float.IsFinite(value))
                {
                    (modifiers ??= []).Add(new StatModifier((StatId)stat, (StatOp)op, value));
                }
            }

            numbers++;
        }

        if ((numbers & 1) != 0)
        {
            throw new JsonException("A modifier list has a stat without a value.");
        }

        return modifiers == null ? NoModifiers : [.. modifiers];
    }

    private static int ReadInt32Element(ref Utf8JsonReader reader, string what)
    {
        if (!reader.Read())
        {
            throw new JsonException($"Unexpected end of JSON while reading the entry {what}.");
        }

        return ReadInt32Token(ref reader, what);
    }

    private static int ReadInt32Token(ref Utf8JsonReader reader, string what)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
        {
            throw new JsonException($"Expected a 32-bit integer for the entry {what}, found {reader.TokenType}.");
        }

        return value;
    }

    private static long ReadInt64Element(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long value))
        {
            throw new JsonException("Expected a 64-bit integer for the entry item id.");
        }

        return value;
    }

    private static string? ReadTextElement(ref Utf8JsonReader reader, string what)
    {
        if (!reader.Read())
        {
            throw new JsonException($"Unexpected end of JSON while reading the entry {what}.");
        }

        return ReadTextToken(ref reader, what);
    }

    private static string? ReadTextToken(ref Utf8JsonReader reader, string what)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string for the entry {what}, found {reader.TokenType}.");
        }

        string? text = reader.GetString();
        if (text != null && text.Length > MaxTextLength)
        {
            throw new JsonException($"The entry {what} is longer than {MaxTextLength} characters.");
        }

        return text;
    }
}
