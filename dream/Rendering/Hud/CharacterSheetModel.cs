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

    // The points and every "+" come from CharacterPanelModel (PlayerStatRules, the rule the server checks).
    public static CharacterSheetRow[] Build(in HudStatusSnapshot s)
    {
        return
        [
            Header("Class", ClassName),
            Header("Level", Int(s.Level)),
            Header("Experience", $"{Int(s.Experience)}/{Int(s.ExperienceToNextLevel)}"),
            Header("Points", Int(CharacterPanelModel.PointsAvailable(s))),
            Attribute(StatId.Strength, "Strength", s.BaseStrength, s.Strength, CharacterPanelModel.CanSpend(s, StatId.Strength)),
            Derived("Damage", $"{Int(s.MinDamage)}-{Int(s.MaxDamage)}"),
            Attribute(StatId.Agility, "Agility", s.BaseAgility, s.Agility, CharacterPanelModel.CanSpend(s, StatId.Agility)),
            Derived("Defense", Int(s.Defense + s.Armor)),
            Derived("Attack Speed", s.AttackSpeed.ToString("0.00", CultureInfo.InvariantCulture) + "/s"),
            Attribute(StatId.Vitality, "Vitality", s.BaseVitality, s.Vitality, CharacterPanelModel.CanSpend(s, StatId.Vitality)),
            Derived("Health", $"{Int(s.CurrentHealth)}/{Int(s.MaxHealth)}"),
            Attribute(StatId.Energy, "Energy", s.BaseEnergy, s.Energy, CharacterPanelModel.CanSpend(s, StatId.Energy)),
            Derived("Mana", $"{Int(s.CurrentMana)}/{Int(s.MaxMana)}"),
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
        return new CharacterSheetRow(CharacterSheetRowKind.Attribute, stat, label, value, canSpend);
    }
}
