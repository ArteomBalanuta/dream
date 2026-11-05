using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;

namespace Dream.Tests;

// The modest attribute baseline the combat and stat-mechanics tests measure against.
//
// The combat harnesses are written around damage and armour magnitude: the player.min_damage formula
// is 5 + strength * 2 and player.max_damage is 10 + strength * 4, so at this baseline's Strength 10
// the range is 25-50, which is 2-4 hits on a cube, and armour and health stay in the modest range
// their numbers were derived in. "It takes several hits" (cube fracture sequences, hit-roll
// divergence, damage-then-checkpoint) needs exactly that.
//
// History: for a while a new character was created at 1000 in each attribute (commit a39702e6), which
// swung for 2005-4010 and one-shot a 100 hp cube, so these helpers were what kept the harnesses
// meaningful. The start is back at PlayerStatRules.NewCharacterAttribute = 10 (the ceiling stays 10000
// as gear headroom), so Profile(...) is now byte-identical to PersistedPlayerProfile.CreateDefault(...)
// and PinTo is a no-op on a profile-backed character with no spends (one that has spent points
// is pinned back to the baseline).
//
// These numbers are the tests' own, on purpose. They must not be wired to the shipping default: the
// point of pinning them is that changing what players start with does not silently rewrite what these
// tests assert. That they currently equal production is a coincidence, not a link.
public static class TestStatBaseline
{
    public const int Attribute = 10;
    public const float AttackSpeed = 2f;

    public static PlayerCombatStats Stats => new()
    {
        Strength = Attribute,
        AttackSpeed = AttackSpeed,
        Vitality = Attribute,
        Agility = Attribute,
        Energy = Attribute
    };

    // A stored profile on the baseline, for the test doubles that stand in for the profile
    // repository. Health is what the player.max_health formula makes of the baseline vitality.
    public static PersistedPlayerProfile Profile(string playerName, DateTime nowUtc)
    {
        PersistedPlayerProfile created = PersistedPlayerProfile.CreateDefault(playerName, nowUtc);
        return created with
        {
            Stats = created.Stats with
            {
                Strength = Attribute,
                Agility = Attribute,
                Vitality = Attribute,
                Energy = Attribute,
                AttackSpeed = AttackSpeed,
                CurrentHealth = 50 + (Attribute * 5),
                MaxHealth = 50 + (Attribute * 5)
            }
        };
    }

    // Writes baseline profile rows before a server is started, so the join finds them and
    // GetOrCreate finds a row instead of creating one with the shipping default. This is how an integration test
    // states the stats its combat, spend or byte expectations were written against.
    public static void SeedProfiles(SqliteSchemaBootstrapper schema, params string[] playerNames)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(playerNames);
        using SqlitePlayerProfileRepository profiles = new(schema);
        DateTime now = DateTime.UtcNow;
        foreach (string playerName in playerNames)
        {
            profiles.Save(Profile(playerName, now));
        }
    }

    // As SeedProfiles, but leaves a row that already exists alone. A harness that restarts the server
    // on the same database calls this before every start: the first call writes the baseline, and a
    // later one must not overwrite the health, stats or deletion the earlier phase left behind.
    public static void SeedMissingProfiles(SqliteSchemaBootstrapper schema, params string[] playerNames)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(playerNames);
        using SqlitePlayerProfileRepository profiles = new(schema);
        DateTime now = DateTime.UtcNow;
        foreach (string playerName in playerNames)
        {
            if (profiles.TryGet(playerName) == null)
            {
                profiles.Save(Profile(playerName, now));
            }
        }
    }

    // Puts a live character on the baseline, so the test below it is about the mechanic and not
    // about where new characters happen to start.
    public static void PinTo(PlayerCombatant combatant)
    {
        ArgumentNullException.ThrowIfNull(combatant);
        combatant.ApplyBaseStats(Stats);
    }
}
