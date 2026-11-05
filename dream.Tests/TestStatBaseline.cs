using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;

namespace Dream.Tests;

// The modest attribute baseline the combat and stat-mechanics tests measure against.
//
// A new character is created at PlayerStatRules.NewCharacterAttribute (1000 in each attribute,
// against a ceiling of 10000, so there is plenty of point headroom). That is not why this helper
// exists. It exists for damage and armour magnitude: the player.min_damage formula is
// 5 + strength * 2 and player.max_damage is 10 + strength * 4, so a fresh character swings for
// 2005-4010, which one-shots a 100 hp cube or a 400 hp enemy. Combat harnesses written around
// "it takes several hits" (cube fracture sequences, hit-roll divergence, damage-then-checkpoint)
// would never see a second damage roll. At this baseline's Strength 10 the range is 25-50, which is
// 2-4 hits on a cube, and armour and health stay in the modest range their numbers were derived in.
//
// These numbers are the tests' own, on purpose. They are not the shipping default and must not be
// wired to it: the point of pinning them is that changing what players start with does not silently
// rewrite what these tests assert.
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
    // GetOrCreate never makes a new character at the ceiling. This is how an integration test
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
