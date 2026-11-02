using System.Globalization;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;

namespace Dream.Rendering.Hud;

public enum CharacterSheetRowKind : byte
{
    // Name block rows: Class, Level, Experience, Points.
    Header,
    // One of the four attributes, with a "+" when it can be raised.
    Attribute,
    // A value that follows from the attribute above it.
    Derived
}

// One line of the character window. Value is the right-hand text, already formatted.
public readonly record struct CharacterSheetRow(
    CharacterSheetRowKind Kind,
    StatId? Attribute,
    string Label,
    string Value,
    bool CanSpend);

// The rows of the character stats window, built from the status snapshot only. Pure: no GL, no
// canvas. Header (class, level, experience, points), then each attribute followed by what it derives.
public static class CharacterSheetModel
{
    public const string ClassName = "Wanderer";

    public static CharacterSheetRow[] Build(in HudStatusSnapshot s)
    {
        bool canSpend = s.StatPoints > 0 && !s.IsDead;
        return
        [
            Header("Class", ClassName),
            Header("Level", Int(s.Level)),
            Header("Experience", $"{Int(s.Experience)}/{Int(s.ExperienceToNextLevel)}"),
            Header("Points", Int(s.StatPoints)),
            Attribute(StatId.Strength, "Strength", s.BaseStrength, s.Strength, canSpend),
            Derived("Damage", $"{Int(s.MinDamage)}-{Int(s.MaxDamage)}"),
            Attribute(StatId.Agility, "Agility", s.BaseAgility, s.Agility, canSpend),
            Derived("Defense", Int(s.Defense + s.Armor)),
            Derived("Attack Speed", s.AttackSpeed.ToString("0.00", CultureInfo.InvariantCulture) + "/s"),
            Attribute(StatId.Vitality, "Vitality", s.BaseVitality, s.Vitality, canSpend),
            Derived("Health", $"{Int(s.CurrentHealth)}/{Int(s.MaxHealth)}"),
            Attribute(StatId.Energy, "Energy", s.BaseEnergy, s.Energy, canSpend),
            Derived("Mana", $"{Int(s.Mana)}/{Int(s.MaxMana)}"),
            Derived("Magic Damage", $"{Int(s.MagicMinDamage)}-{Int(s.MagicMaxDamage)}")
        ];
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static CharacterSheetRow Header(string label, string value)
        => new(CharacterSheetRowKind.Header, null, label, value, false);

    private static CharacterSheetRow Derived(string label, string value)
        => new(CharacterSheetRowKind.Derived, null, label, value, false);

    // "12" when gear adds nothing, "10 +2 = 12" when it does (the base, the bonus, the effective).
    private static CharacterSheetRow Attribute(StatId stat, string label, int baseValue, int effective, bool canSpend)
    {
        int bonus = effective - baseValue;
        string value = baseValue > 0 && bonus != 0
            ? $"{Int(baseValue)} {(bonus > 0 ? "+" : "-")}{Int(Math.Abs(bonus))} = {Int(effective)}"
            : Int(effective);
        bool belowCap = baseValue < StatRules.MaxBase(stat);
        return new CharacterSheetRow(CharacterSheetRowKind.Attribute, stat, label, value, canSpend && belowCap);
    }
}
