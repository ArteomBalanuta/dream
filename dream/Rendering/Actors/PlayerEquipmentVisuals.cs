using Dream.Simulation.Inventory;

namespace Dream.Rendering.Actors;

// What a player holds and wears, as visual ids. A replicated player's ActorSnapshot.VisualKey carries
// nine positional, pipe-separated fields in ascending EquipmentSlot order (Neck and the two rings are
// never replicated): "main|off|head|body|hands|feet|legs|back|companion". Trailing empty fields are
// trimmed on format but the first two are always kept, so a player wearing nothing still sends exactly
// "main|off" (byte-identical to protocol v19) and the key is null when every field is empty. Parse
// accepts 1..9 fields, a strict superset of the v19 form; missing trailing fields are empty.
// The wire is hostile: a key with too many separators or over MaxKeyLength is read as empty, and each
// field is judged on its own - empty, over MaxVisualIdLength, a character outside [a-z0-9._-], or an id
// whose prefix does not belong to its slot (held. for the hands, worn.<slot>. for the rest) is dropped
// alone. Parse allocates only the id strings.
public readonly record struct PlayerEquipmentVisuals(
    string? MainHand = null, string? OffHand = null, string? Head = null,
    string? Body = null, string? Hands = null, string? Feet = null,
    string? Legs = null, string? Back = null, string? Companion = null)
{
    public const char Separator = '|';
    public const int MaxVisualIdLength = 64;
    public const int FieldCount = 9;
    public const int MaxKeyLength = (MaxVisualIdLength * FieldCount) + (FieldCount - 1); // 584

    // The slots the key carries, in wire order.
    public static readonly EquipmentSlot[] KeyFields =
    {
        EquipmentSlot.MainHand, EquipmentSlot.OffHand, EquipmentSlot.Head, EquipmentSlot.Body,
        EquipmentSlot.Hands, EquipmentSlot.Feet, EquipmentSlot.Legs, EquipmentSlot.Back,
        EquipmentSlot.Companion,
    };

    public static PlayerEquipmentVisuals Empty => default;

    public bool IsEmpty => !HasHeld && !HasWorn;

    public bool HasHeld => MainHand is not null || OffHand is not null;

    public bool HasWorn => Head is not null || Body is not null || Hands is not null || Feet is not null
        || Legs is not null || Back is not null || Companion is not null;

    // The visual id of a slot; null for an empty slot and for the slots the key does not carry.
    public string? Get(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.MainHand => MainHand,
        EquipmentSlot.OffHand => OffHand,
        EquipmentSlot.Head => Head,
        EquipmentSlot.Body => Body,
        EquipmentSlot.Hands => Hands,
        EquipmentSlot.Feet => Feet,
        EquipmentSlot.Legs => Legs,
        EquipmentSlot.Back => Back,
        EquipmentSlot.Companion => Companion,
        _ => null,
    };

    public static PlayerEquipmentVisuals Parse(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
        {
            return Empty;
        }

        string?[] fields = new string?[FieldCount];
        int start = 0;
        for (int field = 0; field < FieldCount; field++)
        {
            int end = key.IndexOf(Separator, start);
            bool last = end < 0;
            if (last)
            {
                end = key.Length;
            }

            fields[field] = ParseId(KeyFields[field], key.AsSpan(start, end - start));
            if (last)
            {
                start = -1;
                break;
            }

            start = end + 1;
        }

        // Anything left after the ninth field is a tenth separator or more.
        if (start >= 0)
        {
            return Empty;
        }

        return new PlayerEquipmentVisuals(
            fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], fields[7], fields[8]);
    }

    // The wire form of Parse's input; null when every field is empty.
    public static string? Format(in PlayerEquipmentVisuals visuals)
    {
        if (visuals.IsEmpty)
        {
            return null;
        }

        int count = FieldCount;
        while (count > 2 && string.IsNullOrEmpty(visuals.Get(KeyFields[count - 1])))
        {
            count--;
        }

        if (count == 2)
        {
            return string.Concat(visuals.MainHand, Separator.ToString(), visuals.OffHand);
        }

        string[] parts = new string[count];
        for (int i = 0; i < count; i++)
        {
            parts[i] = visuals.Get(KeyFields[i]) ?? string.Empty;
        }

        return string.Join(Separator, parts);
    }

    public static string? Format(string? mainHand, string? offHand)
    {
        if (string.IsNullOrEmpty(mainHand) && string.IsNullOrEmpty(offHand))
        {
            return null;
        }

        return string.Concat(mainHand, Separator.ToString(), offHand);
    }

    // Local items read from the inventory snapshot: a visual id is kept only when it is well formed and
    // carries its slot's prefix. "held.none" and other non-items are no weapon; a slot the key does not
    // carry (neck, rings) never has a visual.
    public static string? Normalize(EquipmentSlot slot, string? visualId)
    {
        if (Array.IndexOf(KeyFields, slot) < 0 || !IsValidId(visualId))
        {
            return null;
        }

        if (!visualId!.StartsWith(PrefixFor(slot), StringComparison.Ordinal))
        {
            return null;
        }

        return slot is EquipmentSlot.MainHand or EquipmentSlot.OffHand && visualId == "held.none" ? null : visualId;
    }

    public static string? Normalize(string? visualId)
    {
        return IsValidId(visualId) && visualId!.StartsWith("held.", StringComparison.Ordinal) && visualId != "held.none"
            ? visualId
            : null;
    }

    public static string PrefixFor(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.MainHand or EquipmentSlot.OffHand => "held.",
        EquipmentSlot.Head => "worn.head.",
        EquipmentSlot.Body => "worn.body.",
        EquipmentSlot.Hands => "worn.hands.",
        EquipmentSlot.Feet => "worn.feet.",
        EquipmentSlot.Legs => "worn.legs.",
        EquipmentSlot.Neck => "worn.neck.",
        EquipmentSlot.RingLeft or EquipmentSlot.RingRight => "worn.ring.",
        EquipmentSlot.Back => "worn.back.",
        EquipmentSlot.Companion => "worn.companion.",
        _ => "worn.",
    };

    private static string? ParseId(EquipmentSlot slot, ReadOnlySpan<char> id)
    {
        if (id.IsEmpty || id.Length > MaxVisualIdLength)
        {
            return null;
        }

        for (int i = 0; i < id.Length; i++)
        {
            if (!IsIdCharacter(id[i]))
            {
                return null;
            }
        }

        return Normalize(slot, id.ToString());
    }

    private static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxVisualIdLength)
        {
            return false;
        }

        for (int i = 0; i < id.Length; i++)
        {
            if (!IsIdCharacter(id[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdCharacter(char c) =>
        c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
}
