using System.Collections.Concurrent;
using OpenTK.Mathematics;

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

    // The Y extent of the item's mesh in model units AS DRAWN, measured once: the eight corners of every
    // part go through the part's own matrix (size, pitch, roll, centre), so a rolled box counts at its
    // rotated height (the buckler's 45 degree plate is 0.32 * (sin 45 + cos 45) = 0.45 tall, not 0.32).
    // A model with no parts has no extent; it is reported as exactly its target length, i.e. scale 1,
    // rather than as -infinity (which would make Resolve NaN and the item invisible).
    public static float AuthoredLengthModelUnits(string visualId)
    {
        string key = WeaponMeshCatalog.IsKnown(visualId) ? visualId : WeaponMeshCatalog.GenericVisualId;
        return AuthoredLengths.GetOrAdd(key, static id =>
        {
            float extent = RotatedExtentModelUnits(WeaponMeshCatalog.Resolve(id).Parts);
            return extent > 0f ? extent : TargetLengthMetres(id) * ModelUnitsPerMetre;
        });
    }

    // The Y extent of the parts after their own rotations, in model units; 0 when there are no parts.
    public static float RotatedExtentModelUnits(IEnumerable<WeaponPart> parts)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        foreach (WeaponPart part in parts)
        {
            Matrix4 toModel = part.ToMatrix();
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 unit = new(
                    (corner & 1) == 0 ? -0.5f : 0.5f,
                    (corner & 2) == 0 ? -0.5f : 0.5f,
                    (corner & 4) == 0 ? -0.5f : 0.5f);
                float y = Vector3.TransformPosition(unit, toModel).Y;
                min = MathF.Min(min, y);
                max = MathF.Max(max, y);
            }
        }

        float extent = max - min;
        return float.IsFinite(extent) && extent > 0f ? extent : 0f;
    }

    // The uniform scale applied about the grip (or a shield's centre) to the authored mesh.
    public static float Resolve(string visualId)
    {
        string id = WeaponMeshCatalog.IsKnown(visualId) ? visualId : WeaponMeshCatalog.GenericVisualId;
        return TargetLengthMetres(id) * ModelUnitsPerMetre / AuthoredLengthModelUnits(id);
    }
}
