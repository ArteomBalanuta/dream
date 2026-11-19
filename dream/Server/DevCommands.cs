using System.Globalization;

namespace Dream.Server;

public enum DevCommandKind : byte
{
    Help,
    Spawn,
    Teleport,
    Give,
    Clear,
    Heal,
    SetHealth,
    SetMana
}

// One parsed /dev subcommand. Which fields count depends on the kind:
//   Spawn:     Key = enemy key, Count, Distance (metres).
//   Teleport:  Key = "xz" (X, Z), "camp" or "npc" (Key2 = the camp or npc key), or "spawn".
//   Give:      Key = item key, Rarity (null = common), Count.
//   Clear:     Distance = radius in metres, 0 for every enemy.
//   SetHealth, SetMana: Count = the value (the runtime clamps it).
public readonly record struct DevCommand(
    DevCommandKind Kind,
    string Key,
    string? Key2,
    float X,
    float Z,
    int Count,
    float Distance,
    string? Rarity);

// The grammar of one subcommand of a "/dev" line (DevCommandText splits the line). Pure like AdminCommands:
// it decides nothing about who may run it or whether the key exists; the host gates and the runtime resolves.
// Verbs and rarities match ASCII case-insensitively; the enemy, item, camp and npc keys are carried as typed
// and matched case-insensitively where they are resolved.
public static class DevCommands
{
    public const int MaxSpawnCount = 16;

    public const float MinSpawnDistance = 1f;

    public const float MaxSpawnDistance = 16f;

    public const int DefaultSpawnCount = 1;

    public const float DefaultSpawnDistance = 4f;

    public const int MaxGiveCount = 20;

    public const float MaxClearRadius = 1000f;

    // Metres; far past any map, so a typo is refused instead of travelling to the clamp.
    public const float MaxCoordinate = 100000f;

    public const string Usage =
        "Dev: help | spawn <enemy> [1-16] [1-16 m] | tp <x> <z> | tp camp <key> | tp npc <key> | tp spawn | "
        + "give <item> [common|uncommon|rare] [1-20] | clear [radius m] | heal | hp <n> | mana <n>. Chain with ';' (up to 8).";

    private const string SpawnUsage = "Usage: spawn <enemy> [count 1-16] [distance 1-16 m]";

    private const string TeleportUsage = "Usage: tp <x> <z> | tp camp <key> | tp npc <key> | tp spawn";

    private const string GiveUsage = "Usage: give <item> [common|uncommon|rare] [count 1-20]";

    public static bool TryParse(string? subcommand, out DevCommand command, out string error)
    {
        command = default;
        error = Usage;
        if (string.IsNullOrWhiteSpace(subcommand))
        {
            return false;
        }

        string[] tokens = subcommand.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string verb = tokens[0];
        if (Is(verb, "help"))
        {
            command = new DevCommand(DevCommandKind.Help, string.Empty, null, 0f, 0f, 0, 0f, null);
            return true;
        }

        if (Is(verb, "heal"))
        {
            if (tokens.Length != 1)
            {
                error = "Usage: heal";
                return false;
            }

            command = new DevCommand(DevCommandKind.Heal, string.Empty, null, 0f, 0f, 0, 0f, null);
            return true;
        }

        if (Is(verb, "hp") || Is(verb, "mana"))
        {
            bool hp = Is(verb, "hp");
            if (tokens.Length != 2
                || !int.TryParse(tokens[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
            {
                error = hp ? "Usage: hp <n>" : "Usage: mana <n>";
                return false;
            }

            command = new DevCommand(hp ? DevCommandKind.SetHealth : DevCommandKind.SetMana, string.Empty, null, 0f, 0f, value, 0f, null);
            return true;
        }

        if (Is(verb, "clear"))
        {
            float radius = 0f;
            if (tokens.Length > 2 || (tokens.Length == 2 && !TryFloat(tokens[1], 0.1f, MaxClearRadius, out radius)))
            {
                error = $"Usage: clear [radius 0.1-{MaxClearRadius:0} m]";
                return false;
            }

            command = new DevCommand(DevCommandKind.Clear, string.Empty, null, 0f, 0f, 0, radius, null);
            return true;
        }

        if (Is(verb, "spawn"))
        {
            return TryParseSpawn(tokens, out command, out error);
        }

        if (Is(verb, "tp"))
        {
            return TryParseTeleport(tokens, out command, out error);
        }

        if (Is(verb, "give"))
        {
            return TryParseGive(tokens, out command, out error);
        }

        error = $"Unknown dev command '{Clip(verb)}'. {Usage}";
        return false;
    }

    private static bool TryParseSpawn(string[] tokens, out DevCommand command, out string error)
    {
        command = default;
        error = SpawnUsage;
        int count = DefaultSpawnCount;
        float distance = DefaultSpawnDistance;
        if (tokens.Length < 2
            || tokens.Length > 4
            || (tokens.Length >= 3 && !TryInt(tokens[2], 1, MaxSpawnCount, out count))
            || (tokens.Length == 4 && !TryFloat(tokens[3], MinSpawnDistance, MaxSpawnDistance, out distance)))
        {
            return false;
        }

        command = new DevCommand(DevCommandKind.Spawn, tokens[1], null, 0f, 0f, count, distance, null);
        return true;
    }

    private static bool TryParseTeleport(string[] tokens, out DevCommand command, out string error)
    {
        command = default;
        error = TeleportUsage;
        if (tokens.Length == 2 && Is(tokens[1], "spawn"))
        {
            command = new DevCommand(DevCommandKind.Teleport, "spawn", null, 0f, 0f, 0, 0f, null);
            return true;
        }

        if (tokens.Length == 3 && (Is(tokens[1], "camp") || Is(tokens[1], "npc")))
        {
            command = new DevCommand(DevCommandKind.Teleport, tokens[1].ToLowerInvariant(), tokens[2], 0f, 0f, 0, 0f, null);
            return true;
        }

        if (tokens.Length == 3
            && TryFloat(tokens[1], -MaxCoordinate, MaxCoordinate, out float x)
            && TryFloat(tokens[2], -MaxCoordinate, MaxCoordinate, out float z))
        {
            command = new DevCommand(DevCommandKind.Teleport, "xz", null, x, z, 0, 0f, null);
            return true;
        }

        return false;
    }

    private static bool TryParseGive(string[] tokens, out DevCommand command, out string error)
    {
        command = default;
        error = GiveUsage;
        if (tokens.Length < 2 || tokens.Length > 4)
        {
            return false;
        }

        // The rarity and the count are optional and may come in either order, each at most once.
        string? rarity = null;
        int? count = null;
        for (int i = 2; i < tokens.Length; i++)
        {
            if (Is(tokens[i], "common") || Is(tokens[i], "uncommon") || Is(tokens[i], "rare"))
            {
                if (rarity != null)
                {
                    return false;
                }

                rarity = tokens[i].ToLowerInvariant();
            }
            else if (TryInt(tokens[i], 1, MaxGiveCount, out int parsed) && count == null)
            {
                count = parsed;
            }
            else
            {
                return false;
            }
        }

        command = new DevCommand(DevCommandKind.Give, tokens[1], null, 0f, 0f, count ?? 1, 0f, rarity);
        return true;
    }

    private static bool Is(string token, string word) => token.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool TryInt(string token, int min, int max, out int value)
        => int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    private static bool TryFloat(string token, float min, float max, out float value)
        => float.TryParse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            && float.IsFinite(value)
            && value >= min
            && value <= max;

    // A verb typed by the player, shown back in an error: kept short so the reply stays one line.
    private static string Clip(string text) => text.Length <= 24 ? text : text[..24];
}
