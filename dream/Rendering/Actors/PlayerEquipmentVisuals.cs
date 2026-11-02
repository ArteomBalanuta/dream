namespace Dream.Rendering.Actors;

// What a player holds, as the two visual ids shown in the hands. A replicated player's
// ActorSnapshot.VisualKey carries them as "<mainHandVisualId>|<offHandVisualId>"; an empty side is an
// empty hand, and the key is null when both hands are empty. The wire is hostile: a malformed key
// is read as empty hands, and an id with characters outside [a-z0-9._-] or over MaxVisualIdLength
// is dropped on its own. Parse allocates only the two id strings.
public readonly record struct PlayerEquipmentVisuals(string? MainHand, string? OffHand)
{
    public const char Separator = '|';
    public const int MaxVisualIdLength = 64;
    public const int MaxKeyLength = (MaxVisualIdLength * 2) + 1;

    public static PlayerEquipmentVisuals Empty => default;

    public bool IsEmpty => MainHand is null && OffHand is null;

    public static PlayerEquipmentVisuals Parse(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
        {
            return Empty;
        }

        int separator = key.IndexOf(Separator);
        if (separator < 0 || key.IndexOf(Separator, separator + 1) >= 0)
        {
            return Empty;
        }

        return new PlayerEquipmentVisuals(
            ParseId(key.AsSpan(0, separator)),
            ParseId(key.AsSpan(separator + 1)));
    }

    // The wire form of Parse's input; null when both hands are empty.
    public static string? Format(string? mainHand, string? offHand)
    {
        if (string.IsNullOrEmpty(mainHand) && string.IsNullOrEmpty(offHand))
        {
            return null;
        }

        return string.Concat(mainHand, Separator.ToString(), offHand);
    }

    // Local items read from the inventory snapshot: "held.none" and other non-items are no weapon.
    public static string? Normalize(string? visualId)
    {
        return IsValidId(visualId) && visualId!.StartsWith("held.", StringComparison.Ordinal) && visualId != "held.none"
            ? visualId
            : null;
    }

    private static string? ParseId(ReadOnlySpan<char> id)
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

        string text = id.ToString();
        return Normalize(text);
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
