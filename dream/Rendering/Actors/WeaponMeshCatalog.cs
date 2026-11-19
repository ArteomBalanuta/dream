using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;

public enum WeaponMaterial
{
    Steel,
    DarkIron,
    Wood,
    Leather,
    Gilt,
    Cloth,
    Arcane,
    Ember,
    Bone
}

// Where a model is gripped. Hand models have their grip at the origin and their length along +Y
// (+Z is the broad side's normal, X the thin side); they are fixed in the fist. Shield models are
// centred on the origin with the outer face toward +Z, and strapped to the forearm.
public enum WeaponGrip
{
    Hand,
    Shield
}

// One box of a held model: centre and size in model units (the unscaled player model, 2.68 units
// tall = 1.904 m, see HeldItemScale), turned about X (Pitch) and then Z (Roll), in degrees.
public readonly record struct WeaponPart(
    Vector3 Center,
    Vector3 Size,
    WeaponMaterial Material,
    float PitchDegrees = 0f,
    float RollDegrees = 0f,
    EquipmentShape Shape = EquipmentShape.Box,
    float YawDegrees = 0f)
{
    // Local box (unit cube) to model space, for row vectors.
    public Matrix4 ToMatrix()
    {
        Matrix4 matrix = Matrix4.CreateScale(Size);
        if (PitchDegrees != 0f)
        {
            matrix *= Matrix4.CreateRotationX(MathHelper.DegreesToRadians(PitchDegrees));
        }

        if (RollDegrees != 0f)
        {
            matrix *= Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(RollDegrees));
        }

        if (YawDegrees != 0f)
        {
            matrix *= Matrix4.CreateRotationY(MathHelper.DegreesToRadians(YawDegrees));
        }

        return matrix * Matrix4.CreateTranslation(Center);
    }
}

public sealed class WeaponModel(string visualId, WeaponGrip grip, bool isRanged, WeaponPart[] parts)
{
    public string VisualId { get; } = visualId;

    public WeaponGrip Grip { get; } = grip;

    // A bow: the attack plays the shooting pose instead of the melee swing.
    public bool IsRanged { get; } = isRanged;

    public IReadOnlyList<WeaponPart> Parts => parts;

    internal WeaponPart[] PartArray => parts;
}

// The procedural meshes of the items a player holds, by the C4 visual ids. Each is a handful of
// boxes in dark, worn materials (blackened iron, old wood, oiled leather, tarnished brass). An id
// the catalog does not know draws as a plain stick, so a new item is never invisible.
public static class WeaponMeshCatalog
{
    public const string GenericVisualId = "held.generic";

    private static readonly Dictionary<string, WeaponModel> Models = BuildModels();

    private static readonly WeaponModel Generic = new(
        GenericVisualId,
        WeaponGrip.Hand,
        false,
        [
            Box(0f, 0.3f, 0f, 0.06f, 0.8f, 0.06f, WeaponMaterial.Wood),
            Box(0f, 0.72f, 0f, 0.09f, 0.1f, 0.09f, WeaponMaterial.DarkIron)
        ]);

    public static IEnumerable<string> KnownVisualIds => Models.Keys;

    public static WeaponModel Resolve(string visualId)
    {
        return visualId != null && Models.TryGetValue(visualId, out WeaponModel? model) ? model : Generic;
    }

    public static bool IsKnown(string visualId) => visualId != null && Models.ContainsKey(visualId);

    public static bool IsShield(string? visualId) =>
        visualId != null && Models.TryGetValue(visualId, out WeaponModel? model) && model.Grip == WeaponGrip.Shield;

    public static bool IsRanged(string? visualId) =>
        visualId != null && Models.TryGetValue(visualId, out WeaponModel? model) && model.IsRanged;

    // These are ALBEDO, not final pixel colour. The actor shader computes (ambient + lights) * albedo with no
    // tonemap and clamps, and beside a brazier that gain reaches ~2.17, so any channel above ~0.46 clips to a flat
    // patch (the torch used to render a saturated yellow head, the axe a cyan blade). Every channel stays at or
    // under 0.40, enforced by gate_clipping in tools/ui-assets/tests/check_char.py. Materials are told apart by
    // value (dark iron < leather < wood < steel < bone) and by shape, not by hue. The glowing materials keep
    // their glow in Emissive() (added after the gain) and a dark body here: light on a dark piece.
    public static Vector4 BaseColor(WeaponMaterial material) => material switch
    {
        WeaponMaterial.Steel => new Vector4(0.33f, 0.34f, 0.365f, 1f),
        WeaponMaterial.DarkIron => new Vector4(0.11f, 0.11f, 0.125f, 1f),
        WeaponMaterial.Wood => new Vector4(0.20f, 0.125f, 0.07f, 1f),
        WeaponMaterial.Leather => new Vector4(0.13f, 0.08f, 0.06f, 1f),
        WeaponMaterial.Gilt => new Vector4(0.32f, 0.23f, 0.09f, 1f),
        WeaponMaterial.Cloth => new Vector4(0.20f, 0.055f, 0.045f, 1f),
        WeaponMaterial.Arcane => new Vector4(0.10f, 0.06f, 0.17f, 1f),
        WeaponMaterial.Ember => new Vector4(0.24f, 0.10f, 0.04f, 1f),
        WeaponMaterial.Bone => new Vector4(0.40f, 0.36f, 0.29f, 1f),
        _ => new Vector4(0.3f, 0.3f, 0.3f, 1f)
    };

    // Added after lighting: only the glowing materials are not zero.
    public static Vector3 Emissive(WeaponMaterial material) => material switch
    {
        WeaponMaterial.Arcane => new Vector3(0.30f, 0.12f, 0.55f),
        WeaponMaterial.Ember => new Vector3(0.9f, 0.4f, 0.08f),
        _ => Vector3.Zero
    };

    private static WeaponPart Box(
        float x, float y, float z,
        float sx, float sy, float sz,
        WeaponMaterial material,
        float pitch = 0f,
        float roll = 0f,
        EquipmentShape shape = EquipmentShape.Box,
        float yaw = 0f)
    {
        return new WeaponPart(new Vector3(x, y, z), new Vector3(sx, sy, sz), material, pitch, roll, shape, yaw);
    }

    private static Dictionary<string, WeaponModel> BuildModels()
    {
        Dictionary<string, WeaponModel> models = new(StringComparer.Ordinal);

        void Hand(string id, bool ranged, params WeaponPart[] parts) =>
            models[id] = new WeaponModel(id, WeaponGrip.Hand, ranged, parts);

        void Shield(string id, params WeaponPart[] parts) =>
            models[id] = new WeaponModel(id, WeaponGrip.Shield, false, parts);

        // Short, thin, a bone pommel and a hooked guard.
        Hand("held.weapon.dagger", false,
            Box(0f, 0.043f, 0f, 0.07f, 0.118f, 0.07f, WeaponMaterial.Leather),
            Box(0f, -0.027f, 0f, 0.09f, 0.037f, 0.09f, WeaponMaterial.Bone),
            Box(0f, 0.112f, 0f, 0.08f, 0.021f, 0.20f, WeaponMaterial.DarkIron),
            Box(0f, 0.257f, 0f, 0.04f, 0.278f, 0.10f, WeaponMaterial.Steel),
            Box(0f, 0.246f, 0f, 0.05f, 0.182f, 0.03f, WeaponMaterial.DarkIron),
            Box(0f, 0.418f, 0f, 0.035f, 0.054f, 0.05f, WeaponMaterial.Steel));

        // The long blade, a plain cross guard and a round pommel.
        Hand("held.weapon.sword", false,
            Box(0f, 0.081f, 0f, 0.08f, 0.254f, 0.08f, WeaponMaterial.Leather),
            Box(0f, -0.063f, 0f, 0.12f, 0.091f, 0.12f, WeaponMaterial.Gilt),
            Box(0f, 0.226f, 0f, 0.09f, 0.045f, 0.36f, WeaponMaterial.DarkIron),
            Box(0f, 0.706f, 0f, 0.05f, 0.86f, 0.15f, WeaponMaterial.Steel),
            Box(0f, 0.67f, 0f, 0.058f, 0.634f, 0.045f, WeaponMaterial.DarkIron),
            Box(0f, 1.177f, 0f, 0.045f, 0.109f, 0.08f, WeaponMaterial.Steel));

        // A hand-and-a-half grip, wide crossguard with drooping quillons, a broad blade, red wrap.
        Hand("held.weapon.greatsword", false,
            Box(0f, 0.16f, 0f, 0.09f, 0.52f, 0.09f, WeaponMaterial.Leather),
            Box(0f, 0.16f, 0f, 0.10f, 0.14f, 0.10f, WeaponMaterial.Cloth),
            Box(0f, -0.14f, 0f, 0.14f, 0.12f, 0.14f, WeaponMaterial.DarkIron),
            Box(0f, 0.45f, 0f, 0.10f, 0.07f, 0.62f, WeaponMaterial.DarkIron),
            Box(0f, 0.40f, 0.31f, 0.10f, 0.14f, 0.07f, WeaponMaterial.DarkIron),
            Box(0f, 0.40f, -0.31f, 0.10f, 0.14f, 0.07f, WeaponMaterial.DarkIron),
            Box(0f, 1.20f, 0f, 0.06f, 1.40f, 0.24f, WeaponMaterial.Steel),
            Box(0f, 1.14f, 0f, 0.07f, 1.00f, 0.07f, WeaponMaterial.DarkIron),
            Box(0f, 1.95f, 0f, 0.055f, 0.16f, 0.14f, WeaponMaterial.Steel));

        // A short haft with a single broad bit and a back spike.
        Hand("held.weapon.axe", false,
            Box(0f, 0.38f, 0f, 0.07f, 1.05f, 0.07f, WeaponMaterial.Wood),
            Box(0f, -0.14f, 0f, 0.10f, 0.06f, 0.10f, WeaponMaterial.DarkIron),
            Box(0f, 0.86f, 0f, 0.12f, 0.20f, 0.12f, WeaponMaterial.DarkIron),
            Box(0f, 0.86f, 0.21f, 0.05f, 0.40f, 0.32f, WeaponMaterial.Steel),
            Box(0f, 0.86f, 0.38f, 0.045f, 0.50f, 0.06f, WeaponMaterial.Steel),
            Box(0f, 0.86f, -0.14f, 0.05f, 0.08f, 0.18f, WeaponMaterial.DarkIron));

        // Studded round head on a stout haft.
        Hand("held.weapon.mace", false,
            Box(0f, 0.30f, 0f, 0.075f, 0.82f, 0.075f, WeaponMaterial.Wood),
            Box(0f, -0.10f, 0f, 0.11f, 0.08f, 0.11f, WeaponMaterial.Gilt),
            Box(0f, 0.80f, 0f, 0.20f, 0.20f, 0.20f, WeaponMaterial.DarkIron),
            Box(0f, 0.80f, 0f, 0.34f, 0.12f, 0.06f, WeaponMaterial.Steel),
            Box(0f, 0.80f, 0f, 0.06f, 0.12f, 0.34f, WeaponMaterial.Steel),
            Box(0f, 0.80f, 0f, 0.06f, 0.34f, 0.12f, WeaponMaterial.Steel),
            Box(0f, 0.97f, 0f, 0.07f, 0.12f, 0.07f, WeaponMaterial.Steel));

        // A long haft, a heavy block head with a flat face and a back pick.
        Hand("held.weapon.hammer", false,
            Box(0f, 0.45f, 0f, 0.08f, 1.30f, 0.08f, WeaponMaterial.Wood),
            Box(0f, -0.20f, 0f, 0.11f, 0.08f, 0.11f, WeaponMaterial.DarkIron),
            Box(0f, 1.00f, 0f, 0.20f, 0.22f, 0.30f, WeaponMaterial.DarkIron),
            Box(0f, 1.00f, 0.22f, 0.24f, 0.28f, 0.14f, WeaponMaterial.Steel),
            Box(0f, 1.00f, -0.22f, 0.07f, 0.09f, 0.22f, WeaponMaterial.Steel),
            Box(0f, 0.78f, 0f, 0.11f, 0.06f, 0.11f, WeaponMaterial.Gilt));

        // A long shaft with a leaf head, a red tassel and an iron butt.
        Hand("held.weapon.spear", false,
            Box(0f, 0.62f, 0f, 0.06f, 2.10f, 0.06f, WeaponMaterial.Wood),
            Box(0f, -0.42f, 0f, 0.09f, 0.10f, 0.09f, WeaponMaterial.DarkIron),
            Box(0f, 1.68f, 0f, 0.10f, 0.12f, 0.10f, WeaponMaterial.Cloth),
            Box(0f, 1.74f, 0f, 0.08f, 0.10f, 0.08f, WeaponMaterial.DarkIron),
            Box(0f, 1.86f, 0f, 0.05f, 0.20f, 0.15f, WeaponMaterial.Steel),
            Box(0f, 2.02f, 0f, 0.045f, 0.16f, 0.09f, WeaponMaterial.Steel),
            Box(0f, 2.14f, 0f, 0.04f, 0.10f, 0.04f, WeaponMaterial.Steel));

        // Bent limbs toward the archer (-Z) and a taut string.
        Hand("held.weapon.bow", true,
            Box(0f, 0f, 0f, 0.08f, 0.26f, 0.08f, WeaponMaterial.Leather),
            Box(0f, 0.30f, -0.03f, 0.06f, 0.36f, 0.06f, WeaponMaterial.Wood, pitch: -14f),
            Box(0f, -0.30f, -0.03f, 0.06f, 0.36f, 0.06f, WeaponMaterial.Wood, pitch: 14f),
            Box(0f, 0.62f, -0.13f, 0.055f, 0.34f, 0.055f, WeaponMaterial.Wood, pitch: -32f),
            Box(0f, -0.62f, -0.13f, 0.055f, 0.34f, 0.055f, WeaponMaterial.Wood, pitch: 32f),
            Box(0f, 0.80f, -0.24f, 0.07f, 0.07f, 0.07f, WeaponMaterial.Gilt),
            Box(0f, -0.80f, -0.24f, 0.07f, 0.07f, 0.07f, WeaponMaterial.Gilt),
            Box(0f, 0f, -0.24f, 0.015f, 1.58f, 0.015f, WeaponMaterial.Bone));

        // A tall dark staff with an iron claw around a glowing stone.
        Hand("held.weapon.staff", false,
            Box(0f, 0.72f, 0f, 0.07f, 2.00f, 0.07f, WeaponMaterial.Wood),
            Box(0f, 0.05f, 0f, 0.09f, 0.34f, 0.09f, WeaponMaterial.Leather),
            Box(0f, -0.30f, 0f, 0.09f, 0.08f, 0.09f, WeaponMaterial.DarkIron),
            Box(0f, 1.70f, 0f, 0.13f, 0.08f, 0.13f, WeaponMaterial.DarkIron),
            Box(0.085f, 1.86f, 0f, 0.035f, 0.34f, 0.035f, WeaponMaterial.DarkIron),
            Box(-0.085f, 1.86f, 0f, 0.035f, 0.34f, 0.035f, WeaponMaterial.DarkIron),
            Box(0f, 1.86f, 0.085f, 0.035f, 0.34f, 0.035f, WeaponMaterial.DarkIron),
            Box(0f, 1.86f, -0.085f, 0.035f, 0.34f, 0.035f, WeaponMaterial.DarkIron),
            Box(0f, 1.88f, 0f, 0.13f, 0.20f, 0.13f, WeaponMaterial.Arcane, roll: 45f));

        // A plain arming sword: a short broad blade, a gilt guard and a bone pommel.
        Hand("held.weapon.short-sword", false,
            Box(0f, 0.08f, 0f, 0.08f, 0.26f, 0.08f, WeaponMaterial.Leather),
            Box(0f, -0.06f, 0f, 0.10f, 0.08f, 0.10f, WeaponMaterial.Bone),
            Box(0f, 0.23f, 0f, 0.09f, 0.05f, 0.28f, WeaponMaterial.Gilt),
            Box(0f, 0.58f, 0f, 0.05f, 0.66f, 0.17f, WeaponMaterial.Steel),
            Box(0f, 0.54f, 0f, 0.058f, 0.48f, 0.05f, WeaponMaterial.DarkIron),
            Box(0f, 0.94f, 0f, 0.04f, 0.10f, 0.09f, WeaponMaterial.Steel));

        // A heavy, broad single-edged blade with a dark spine and a clipped, angled tip.
        Hand("held.weapon.iron-blade", false,
            Box(0f, 0.09f, 0f, 0.08f, 0.28f, 0.08f, WeaponMaterial.Leather),
            Box(0f, 0.25f, 0f, 0.09f, 0.05f, 0.16f, WeaponMaterial.DarkIron),
            Box(0f, 0.68f, 0f, 0.06f, 0.80f, 0.22f, WeaponMaterial.Steel),
            Box(0f, 0.66f, -0.10f, 0.07f, 0.72f, 0.04f, WeaponMaterial.DarkIron),
            Box(0f, 1.12f, 0.03f, 0.05f, 0.18f, 0.14f, WeaponMaterial.Steel, pitch: -18f));

        // A short haft and a small bearded bit.
        Hand("held.weapon.hand-axe", false,
            Box(0f, 0.165f, 0f, 0.06f, 0.576f, 0.06f, WeaponMaterial.Wood),
            Box(0f, -0.132f, 0f, 0.09f, 0.041f, 0.09f, WeaponMaterial.DarkIron),
            Box(0f, 0.478f, 0.02f, 0.10f, 0.132f, 0.10f, WeaponMaterial.DarkIron),
            Box(0f, 0.478f, 0.16f, 0.045f, 0.231f, 0.20f, WeaponMaterial.Steel),
            Box(0f, 0.478f, 0.27f, 0.04f, 0.28f, 0.04f, WeaponMaterial.Steel));

        // A small recurved bow with bone tips and a short string.
        Hand("held.weapon.short-bow", true,
            Box(0f, 0f, 0f, 0.08f, 0.22f, 0.08f, WeaponMaterial.Leather),
            Box(0f, 0.24f, -0.02f, 0.05f, 0.30f, 0.05f, WeaponMaterial.Wood, pitch: -12f),
            Box(0f, -0.24f, -0.02f, 0.05f, 0.30f, 0.05f, WeaponMaterial.Wood, pitch: 12f),
            Box(0f, 0.46f, -0.10f, 0.05f, 0.26f, 0.05f, WeaponMaterial.Wood, pitch: -30f),
            Box(0f, -0.46f, -0.10f, 0.05f, 0.26f, 0.05f, WeaponMaterial.Wood, pitch: 30f),
            Box(0f, 0.60f, -0.19f, 0.06f, 0.06f, 0.06f, WeaponMaterial.Bone),
            Box(0f, -0.60f, -0.19f, 0.06f, 0.06f, 0.06f, WeaponMaterial.Bone),
            Box(0f, 0f, -0.19f, 0.015f, 1.20f, 0.015f, WeaponMaterial.Bone));

        // A pale bone shaft with a skull cap and a cold arcane stone.
        Hand("held.weapon.bone-staff", false,
            Box(0f, 0.60f, 0f, 0.08f, 1.70f, 0.08f, WeaponMaterial.Bone),
            Box(0f, 0.05f, 0f, 0.10f, 0.30f, 0.10f, WeaponMaterial.Leather),
            Box(0f, -0.28f, 0f, 0.10f, 0.08f, 0.10f, WeaponMaterial.DarkIron),
            Box(0f, 1.55f, 0f, 0.18f, 0.16f, 0.20f, WeaponMaterial.Bone),
            Box(0f, 1.56f, 0.105f, 0.12f, 0.05f, 0.02f, WeaponMaterial.DarkIron),
            Box(0f, 1.70f, 0f, 0.09f, 0.12f, 0.09f, WeaponMaterial.Arcane, roll: 45f));

        // A gilt cage lantern with a cold arcane flame and a handle.
        Hand("held.gear.ward-lantern", false,
            Box(0f, 0.22f, 0f, 0.03f, 0.26f, 0.03f, WeaponMaterial.DarkIron),
            Box(0f, 0.08f, 0f, 0.18f, 0.06f, 0.18f, WeaponMaterial.Gilt),
            Box(0f, -0.02f, 0f, 0.12f, 0.18f, 0.12f, WeaponMaterial.Arcane),
            Box(0.075f, -0.02f, 0.075f, 0.025f, 0.22f, 0.025f, WeaponMaterial.Gilt),
            Box(-0.075f, -0.02f, 0.075f, 0.025f, 0.22f, 0.025f, WeaponMaterial.Gilt),
            Box(0.075f, -0.02f, -0.075f, 0.025f, 0.22f, 0.025f, WeaponMaterial.Gilt),
            Box(-0.075f, -0.02f, -0.075f, 0.025f, 0.22f, 0.025f, WeaponMaterial.Gilt),
            Box(0f, -0.13f, 0f, 0.18f, 0.04f, 0.18f, WeaponMaterial.Gilt));

        // Small round plate with a boss.
        Shield("held.shield.buckler",
            Box(0f, 0f, 0f, 0.40f, 0.28f, 0.05f, WeaponMaterial.Wood),
            Box(0f, 0f, 0f, 0.28f, 0.40f, 0.05f, WeaponMaterial.Wood),
            Box(0f, 0f, 0.02f, 0.32f, 0.32f, 0.05f, WeaponMaterial.DarkIron, roll: 45f),
            Box(0f, 0f, 0.06f, 0.14f, 0.14f, 0.06f, WeaponMaterial.Steel),
            Box(0f, 0f, 0.10f, 0.05f, 0.05f, 0.05f, WeaponMaterial.Gilt));

        // Tapering heater shape, a steel rim and a red cross.
        Shield("held.shield.kite",
            Box(0f, 0.18f, 0f, 0.52f, 0.34f, 0.05f, WeaponMaterial.Wood),
            Box(0f, -0.08f, 0f, 0.40f, 0.26f, 0.05f, WeaponMaterial.Wood),
            Box(0f, -0.30f, 0f, 0.24f, 0.22f, 0.05f, WeaponMaterial.Wood),
            Box(0f, -0.48f, 0f, 0.10f, 0.14f, 0.05f, WeaponMaterial.Wood),
            Box(0f, 0.36f, 0.02f, 0.56f, 0.05f, 0.07f, WeaponMaterial.Steel),
            Box(0f, 0.04f, 0.035f, 0.07f, 0.80f, 0.04f, WeaponMaterial.Cloth),
            Box(0f, 0.16f, 0.035f, 0.42f, 0.07f, 0.04f, WeaponMaterial.Cloth),
            Box(0f, 0.10f, 0.07f, 0.12f, 0.12f, 0.06f, WeaponMaterial.Steel));

        // A door-sized slab with an iron frame and bands.
        Shield("held.shield.tower",
            Box(0f, 0f, 0f, 0.64f, 1.04f, 0.06f, WeaponMaterial.Wood),
            Box(0f, 0.53f, 0.02f, 0.70f, 0.06f, 0.09f, WeaponMaterial.DarkIron),
            Box(0f, -0.53f, 0.02f, 0.70f, 0.06f, 0.09f, WeaponMaterial.DarkIron),
            Box(0.33f, 0f, 0.02f, 0.06f, 1.10f, 0.09f, WeaponMaterial.DarkIron),
            Box(-0.33f, 0f, 0.02f, 0.06f, 1.10f, 0.09f, WeaponMaterial.DarkIron),
            Box(0f, 0.20f, 0.04f, 0.64f, 0.06f, 0.07f, WeaponMaterial.DarkIron),
            Box(0f, -0.20f, 0.04f, 0.64f, 0.06f, 0.07f, WeaponMaterial.DarkIron),
            Box(0f, 0f, 0.08f, 0.18f, 0.18f, 0.07f, WeaponMaterial.Steel),
            Box(0f, 0f, 0.12f, 0.06f, 0.06f, 0.05f, WeaponMaterial.Gilt));

        // The older tool visuals.
        Hand("held.tool.blade", false,
            Box(0f, 0.08f, 0f, 0.07f, 0.22f, 0.07f, WeaponMaterial.Leather),
            Box(0f, 0.20f, 0f, 0.08f, 0.04f, 0.26f, WeaponMaterial.Gilt),
            Box(0f, 0.62f, 0f, 0.05f, 0.80f, 0.12f, WeaponMaterial.Steel),
            Box(0f, 1.05f, 0f, 0.04f, 0.10f, 0.06f, WeaponMaterial.Steel));

        Hand("held.tool.maul", false,
            Box(0f, 0.40f, 0f, 0.09f, 1.20f, 0.09f, WeaponMaterial.Wood),
            Box(0f, 1.05f, 0f, 0.30f, 0.30f, 0.42f, WeaponMaterial.DarkIron),
            Box(0f, 1.05f, 0.22f, 0.32f, 0.34f, 0.06f, WeaponMaterial.Steel),
            Box(0f, 1.05f, -0.22f, 0.32f, 0.34f, 0.06f, WeaponMaterial.Steel));

        Hand("held.tool.builder", false,
            Box(0f, 0.20f, 0f, 0.07f, 0.55f, 0.07f, WeaponMaterial.Wood),
            Box(0f, 0.55f, 0.05f, 0.06f, 0.22f, 0.22f, WeaponMaterial.Steel),
            Box(0f, 0.50f, 0f, 0.09f, 0.08f, 0.09f, WeaponMaterial.DarkIron));

        Hand("held.tool.torch", false,
            Box(0f, 0.169f, 0f, 0.07f, 0.479f, 0.07f, WeaponMaterial.Wood),
            Box(0f, 0.44f, 0f, 0.12f, 0.09f, 0.12f, WeaponMaterial.Leather),
            Box(0f, 0.536f, 0f, 0.13f, 0.102f, 0.13f, WeaponMaterial.DarkIron),
            Box(0f, 0.604f, 0f, 0.09f, 0.09f, 0.09f, WeaponMaterial.Ember, roll: 45f),
            Box(0f, 0.666f, 0f, 0.05f, 0.068f, 0.05f, WeaponMaterial.Ember, roll: 45f));

        Hand("held.tool.lantern", false,
            Box(0f, 0.20f, 0f, 0.025f, 0.30f, 0.025f, WeaponMaterial.DarkIron),
            Box(0f, 0.05f, 0f, 0.20f, 0.04f, 0.20f, WeaponMaterial.DarkIron),
            Box(0f, 0.00f, 0f, 0.14f, 0.16f, 0.14f, WeaponMaterial.Ember),
            Box(0.085f, 0f, 0f, 0.025f, 0.20f, 0.025f, WeaponMaterial.DarkIron),
            Box(-0.085f, 0f, 0f, 0.025f, 0.20f, 0.025f, WeaponMaterial.DarkIron),
            Box(0f, -0.10f, 0f, 0.20f, 0.04f, 0.20f, WeaponMaterial.DarkIron));

        return models;
    }
}
