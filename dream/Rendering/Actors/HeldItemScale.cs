using System.Collections.Concurrent;

namespace Dream.Rendering.Actors;

// How large a held item is drawn. Every item has a documented real-world length (TargetLengthMetres),
// and the scale that makes its authored mesh come out at that length on the authored figure is
// Resolve. This replaces the old blanket 1.7x / 1.5x, which made a dagger as long as a short sword
// and a tower shield taller than the figure's torso.
public static class HeldItemScale
{
    // 1 m = 1.408 model units: tools/ui-assets/gen_char_player.py:47 U_PER_M = 16.0 (art units per
    // metre) times :56 UNITS_PER_U = 0.088 (model units per art unit).
    public const float ModelUnitsPerMetre = 1.408f;

    // dream/animations/players/player-grim.glb measures 2.6805 model units tall; 2.6805 / 1.408 = 1.904 m.
    public const float AuthoredFigureHeightMetres = 1.904f;

    private static readonly ConcurrentDictionary<string, float> AuthoredLengths = new(StringComparer.Ordinal);

    // The real-world reference length of the item in metres (the blade, the haft, the shield's height).
    public static float TargetLengthMetres(string visualId) => visualId switch
    {
        "held.weapon.dagger" => 0.35f,
        "held.weapon.short-sword" => 0.75f,
        "held.weapon.sword" => 0.95f,
        "held.weapon.greatsword" => 1.55f,
        "held.weapon.axe" => 0.90f,
        "held.weapon.hand-axe" => 0.55f,
        "held.weapon.mace" => 0.75f,
        "held.weapon.hammer" => 1.00f,
        "held.weapon.spear" => 2.10f,
        "held.weapon.bow" => 1.70f,
        "held.weapon.short-bow" => 1.10f,
        "held.weapon.staff" => 1.80f,
        "held.weapon.bone-staff" => 1.65f,
        "held.weapon.iron-blade" => 0.90f,
        "held.gear.ward-lantern" => 0.35f,
        "held.shield.buckler" => 0.40f,
        "held.shield.kite" => 0.95f,
        "held.shield.tower" => 1.15f,
        "held.tool.blade" => 0.80f,
        "held.tool.maul" => 1.00f,
        "held.tool.builder" => 0.55f,
        "held.tool.torch" => 0.55f,
        "held.tool.lantern" => 0.32f,
        _ => 0.80f
    };

    // The Y extent of the item's mesh in model units, measured once. Each part contributes
    // Center.Y +/- Size.Y / 2; part rotations are ignored (a pitched limb is a little shorter along
    // Y, a rolled box a little taller, and either is a few percent on one small part).
    public static float AuthoredLengthModelUnits(string visualId)
    {
        string key = WeaponMeshCatalog.IsKnown(visualId) ? visualId : WeaponMeshCatalog.GenericVisualId;
        return AuthoredLengths.GetOrAdd(key, static id =>
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            foreach (WeaponPart part in WeaponMeshCatalog.Resolve(id).Parts)
            {
                min = MathF.Min(min, part.Center.Y - (part.Size.Y * 0.5f));
                max = MathF.Max(max, part.Center.Y + (part.Size.Y * 0.5f));
            }

            return max - min;
        });
    }

    // The uniform scale applied about the grip (or a shield's centre) to the authored mesh.
    public static float Resolve(string visualId)
    {
        string id = WeaponMeshCatalog.IsKnown(visualId) ? visualId : WeaponMeshCatalog.GenericVisualId;
        return TargetLengthMetres(id) * ModelUnitsPerMetre / AuthoredLengthModelUnits(id);
    }
}
