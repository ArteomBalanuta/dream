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

    // Puts a live character on the baseline, so the test below it is about the mechanic and not
    // about where new characters happen to start.
    public static void PinTo(PlayerCombatant combatant)
    {
        ArgumentNullException.ThrowIfNull(combatant);
        combatant.ApplyBaseStats(Stats);
    }
}
