using System.Globalization;
using Dream.Simulation.World;

namespace Dream.Server.Worlds;

// Which world a server opens: the default (neither set), one id, or one name.
public readonly record struct WorldSelector(WorldId? Id, string? Name)
{
    public static WorldSelector Default => default;

    public bool IsDefault => Id is null && Name is null;

    // Null or blank selects the default world; all ASCII digits select an id; anything else is a
    // trimmed name. An all-digit text that is not a valid id (0, or too large for a long) throws
    // ArgumentException.
    public static WorldSelector Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }

        string trimmed = text.Trim();
        if (trimmed.All(char.IsAsciiDigit))
        {
            if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value < 1)
            {
                throw new ArgumentException($"'{trimmed}' is not a valid world id.", nameof(text));
            }

            return new WorldSelector(new WorldId(value), null);
        }

        return new WorldSelector(null, trimmed);
    }

    public override string ToString() => Id?.ToString() ?? Name ?? "(default)";
}
