using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;

namespace Dream.Tests;

// The modest attribute baseline the stat-mechanics tests measure against.
//
// A new character is created at PlayerStatRules.NewCharacterAttribute, which is also each
// attribute's ceiling (PlayerCombatStats.MaxStrength and friends), so it has no headroom: a spend
// cannot raise anything, and PointsAvailable is 0 because the character already reads as far over
// budget. A test about the point system, about armor from Agility, or about what a spend does to a
// stored row therefore has to say what the character starts at rather than take whatever new
// characters are handed today.
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

    // Puts a live character on the baseline, so the test below it is about the mechanic and not
    // about where new characters happen to start.
    public static void PinTo(PlayerCombatant combatant)
    {
        ArgumentNullException.ThrowIfNull(combatant);
        combatant.ApplyBaseStats(Stats);
    }
}
