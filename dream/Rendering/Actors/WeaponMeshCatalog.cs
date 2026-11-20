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

// One part of a held model: centre and size in model units (the unscaled player model, 2.68 units
// tall = 1.904 m, see HeldItemScale), turned about X (Pitch), then Z (Roll), then Y (Yaw), in degrees.
// Shape is the sculpted primitive the part is drawn with; its mesh is inscribed in the same box the
// centre and size describe, so Size is still the part's real extent whatever the shape.
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
// SCULPTED parts (EquipmentShape) in dark, worn materials (blackened iron, old wood, oiled leather,
// tarnished brass): a haft is a Shaft, a blade is a Blade, an axe bit a Crescent, a pommel a Dome or
// a Ring, a ferrule or quillon tip a Taper, a stud a Spike, a cloth a Panel, an arcane stone a Gem.
// A plain Box survives nowhere in this catalog; anything genuinely rectilinear (a shield's iron band,
// a hammer head, a fuller) is a BevelBox, whose chamfers catch a different light value than its faces.
// An id the catalog does not know draws as a plain stick, so a new item is never invisible.
//
// Budget: at most ~14 parts per model. At the shipped isometric zoom the whole figure is 28x101 px, so
// silhouette, mass and value are what read; curvature is worth paying for (a curved surface gives a run
// of light where a box gives one flat value), ornament smaller than a pixel is not.
//
// Axis conventions when choosing a rotation. A Crescent sweeps in YZ with a horn at each end of Y and
// its convex (ground) edge toward +Z, which is an axe bit as authored; pitch: 90f turns the horns onto
// +-Z and drops the convex edge to -Y, which is a drooping gothic crossguard. A Spike points along its
// own +Y, so pitch: +-90f aims it along +-Z and roll: -+90f along +-X. A Dome caps its own +Y, so
// roll: 180f makes a pommel cap and pitch: 90f a hammer's crowned face. Sizes are always authored in
// the part's OWN frame, before the rotation.
public static class WeaponMeshCatalog
{
    public const string GenericVisualId = "held.generic";

    private static readonly Dictionary<string, WeaponModel> Models = BuildModels();

    private static readonly WeaponModel Generic = new(
        GenericVisualId,
        WeaponGrip.Hand,
        false,
        [
            Box(0f, 0.3f, 0f, 0.06f, 0.8f, 0.06f, WeaponMaterial.Wood, shape: EquipmentShape.Shaft),
            Box(0f, 0.72f, 0f, 0.09f, 0.1f, 0.09f, WeaponMaterial.DarkIron, shape: EquipmentShape.Taper)
        ]);

    public static IEnumerable<string> KnownVisualIds => Models.Keys;

    public static WeaponModel Resolve(string visualId)
    {
        return visualId != null && Models.TryGetValue(visualId, out WeaponModel? model) ? model : Generic;
    }

    // Where the flame of a light-casting held item burns, in the item's own frame (before the grip and the
    // item scale): the held light is anchored here so it rides the item the hand actually carries. An explicit
    // table, one row per lit item; false for every other id.
    public static bool TryGetFlamePoint(string? visualId, out Vector3 itemPoint)
    {
        switch (visualId)
        {
            case "held.tool.torch":
                itemPoint = new Vector3(0f, 0.62f, 0f);
                return true;
            case "held.tool.lantern":
                itemPoint = Vector3.Zero;
                return true;
            case "held.gear.ward-lantern":
                itemPoint = new Vector3(0f, -0.02f, 0f);
                return true;
            default:
                itemPoint = Vector3.Zero;
                return false;
        }
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

    // Added after the lighting gain, and nothing shades it: an emissive at or above the lit value pins that
    // fraction of the surface flat. That cost is acceptable on the Gem parts (0.09-0.20 world units on a
    // 1.2-unit figure, a handful of pixels at the isometric zoom, already emissive-dominated) and is not
    // acceptable over a large area, which is why ArmourMeshCatalog's Membrane entry (0.09, 0.035, 0.17) is
    // about a sixth of this (0.164, 0.159 and 0.17 per channel); it was a third of the old (0.30, 0.12, 0.55).
    // The Arcane peak sits above PostSettings.Knee so the overflow feeds the bloom and the gem glows.
    // Only the glowing materials are not zero.
    public static Vector3 Emissive(WeaponMaterial material) => material switch
    {
        WeaponMaterial.Arcane => new Vector3(0.55f, 0.22f, 1.00f),
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

        const EquipmentShape BevelBox = EquipmentShape.BevelBox;
        const EquipmentShape Shaft = EquipmentShape.Shaft;
        const EquipmentShape Taper = EquipmentShape.Taper;
        const EquipmentShape Blade = EquipmentShape.Blade;
        const EquipmentShape Plate = EquipmentShape.Plate;
        const EquipmentShape Dome = EquipmentShape.Dome;
        const EquipmentShape Spike = EquipmentShape.Spike;
        const EquipmentShape Ring = EquipmentShape.Ring;
        const EquipmentShape Crescent = EquipmentShape.Crescent;
        const EquipmentShape Gem = EquipmentShape.Gem;
        const EquipmentShape Panel = EquipmentShape.Panel;
        const EquipmentShape Horn = EquipmentShape.Horn;

        // Short and thin: a ring pommel, a rolled grip, a drooping hooked guard and a narrow fullered blade.
        Hand("held.weapon.dagger", false,
            Box(0f, 0.043f, 0f, 0.07f, 0.118f, 0.07f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, -0.027f, 0f, 0.09f, 0.037f, 0.09f, WeaponMaterial.Bone, shape: Ring),
            Box(0f, 0.062f, 0f, 0.075f, 0.018f, 0.075f, WeaponMaterial.DarkIron, shape: Ring),
            Box(0f, 0.112f, 0.052f, 0.030f, 0.115f, 0.030f, WeaponMaterial.DarkIron, pitch: 90f, shape: Horn),
            Box(0f, 0.112f, -0.052f, 0.030f, 0.115f, 0.030f, WeaponMaterial.DarkIron, pitch: 90f, yaw: 180f, shape: Horn),
            Box(0f, 0.136f, 0f, 0.055f, 0.03f, 0.055f, WeaponMaterial.Steel, shape: Taper),
            Box(0f, 0.257f, 0f, 0.04f, 0.278f, 0.10f, WeaponMaterial.Steel, shape: Blade),
            Box(0f, 0.246f, 0f, 0.05f, 0.182f, 0.03f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 0.418f, 0f, 0.035f, 0.054f, 0.05f, WeaponMaterial.Steel, shape: Blade));

        // The long blade: drooping quillons with spiked tips, a gilt langet and a domed pommel on a ring.
        Hand("held.weapon.sword", false,
            Box(0f, 0.081f, 0f, 0.08f, 0.254f, 0.08f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, -0.063f, 0f, 0.12f, 0.091f, 0.12f, WeaponMaterial.Gilt, roll: 180f, shape: Dome),
            Box(0f, -0.005f, 0f, 0.105f, 0.025f, 0.105f, WeaponMaterial.Gilt, shape: Ring),
            Box(0f, 0.226f, 0f, 0.09f, 0.36f, 0.045f, WeaponMaterial.DarkIron, pitch: 90f, shape: Crescent),
            Box(0f, 0.226f, 0.175f, 0.045f, 0.05f, 0.045f, WeaponMaterial.Steel, pitch: 90f, shape: Spike),
            Box(0f, 0.226f, -0.175f, 0.045f, 0.05f, 0.045f, WeaponMaterial.Steel, pitch: -90f, shape: Spike),
            Box(0f, 0.29f, 0f, 0.085f, 0.16f, 0.085f, WeaponMaterial.Gilt, shape: EquipmentShape.Lame),
            Box(0f, 0.706f, 0f, 0.05f, 0.86f, 0.15f, WeaponMaterial.Steel, shape: Blade),
            Box(0f, 0.67f, 0f, 0.058f, 0.634f, 0.045f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 1.177f, 0f, 0.045f, 0.109f, 0.08f, WeaponMaterial.Steel, shape: Blade));

        // A hand-and-a-half: a red-wrapped grip, a wide drooping crossguard with a parrying ring over a
        // leather ricasso, and a broad blade. The outline is the guard, not the length.
        Hand("held.weapon.greatsword", false,
            Box(0f, 0.16f, 0f, 0.09f, 0.52f, 0.09f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 0.16f, 0f, 0.105f, 0.14f, 0.105f, WeaponMaterial.Cloth, shape: Ring),
            Box(0f, -0.14f, 0f, 0.14f, 0.12f, 0.14f, WeaponMaterial.DarkIron, roll: 180f, shape: Dome),
            Box(0f, -0.21f, 0f, 0.07f, 0.07f, 0.07f, WeaponMaterial.Gilt, shape: Gem),
            Box(0f, 0.45f, 0f, 0.10f, 0.62f, 0.07f, WeaponMaterial.DarkIron, pitch: 90f, shape: Crescent),
            Box(0f, 0.40f, 0.31f, 0.09f, 0.14f, 0.09f, WeaponMaterial.DarkIron, pitch: 180f, shape: Taper),
            Box(0f, 0.40f, -0.31f, 0.09f, 0.14f, 0.09f, WeaponMaterial.DarkIron, pitch: 180f, shape: Taper),
            Box(0f, 0.52f, 0f, 0.16f, 0.05f, 0.16f, WeaponMaterial.Gilt, shape: Ring),
            Box(0f, 0.56f, 0f, 0.07f, 0.16f, 0.07f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 0.66f, 0f, 0.065f, 0.14f, 0.10f, WeaponMaterial.Gilt, shape: Taper),
            Box(0f, 1.20f, 0f, 0.06f, 1.40f, 0.24f, WeaponMaterial.Steel, shape: Blade),
            Box(0f, 1.14f, 0f, 0.07f, 1.00f, 0.07f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 1.95f, 0f, 0.055f, 0.16f, 0.14f, WeaponMaterial.Steel, shape: Blade));

        // A short haft and one broad bearded bit: the Crescent's horns and ground convex edge ARE the axe.
        Hand("held.weapon.axe", false,
            Box(0f, 0.38f, 0f, 0.07f, 1.05f, 0.07f, WeaponMaterial.Wood, shape: Shaft),
            Box(0f, -0.14f, 0f, 0.10f, 0.06f, 0.10f, WeaponMaterial.DarkIron, pitch: 180f, shape: Taper),
            Box(0f, 0.05f, 0f, 0.085f, 0.30f, 0.085f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 0.86f, 0f, 0.12f, 0.20f, 0.12f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 0.86f, 0.21f, 0.05f, 0.40f, 0.32f, WeaponMaterial.Steel, shape: Crescent),
            Box(0f, 0.86f, 0.38f, 0.045f, 0.50f, 0.06f, WeaponMaterial.Steel, shape: Crescent),
            Box(0f, 0.86f, -0.14f, 0.05f, 0.18f, 0.08f, WeaponMaterial.DarkIron, pitch: -90f, shape: Spike),
            Box(0f, 0.70f, 0f, 0.105f, 0.22f, 0.105f, WeaponMaterial.DarkIron, shape: EquipmentShape.Lame),
            Box(0f, 0.86f, 0.10f, 0.06f, 0.03f, 0.06f, WeaponMaterial.Steel, pitch: 90f, shape: Dome),
            Box(0f, 1.00f, 0f, 0.05f, 0.12f, 0.05f, WeaponMaterial.Steel, shape: Spike));

        // Flanged head: at the 28x101 px actor the old head - a dome with four EQUAL flanges at the four
        // compass points - collapsed into a symmetric blob on a stick, because a symmetric outline carries no
        // information about which way the weapon faces. The head now has an asymmetric profile: two broad
        // side flanges that stand well out, a long rear pick and a shorter, lower front beak.
        Hand("held.weapon.mace", false,
            Box(0f, 0.30f, 0f, 0.075f, 0.82f, 0.075f, WeaponMaterial.Wood, shape: Shaft),
            Box(0f, -0.10f, 0f, 0.11f, 0.08f, 0.11f, WeaponMaterial.Gilt, pitch: 180f, shape: Taper),
            Box(0f, 0.08f, 0f, 0.09f, 0.26f, 0.09f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 0.64f, 0f, 0.10f, 0.04f, 0.10f, WeaponMaterial.Gilt, shape: Ring),
            Box(0f, 0.80f, 0f, 0.20f, 0.20f, 0.20f, WeaponMaterial.DarkIron, shape: Dome),
            Box(0.17f, 0.80f, 0f, 0.16f, 0.20f, 0.05f, WeaponMaterial.Steel, roll: -90f, shape: Spike),
            Box(-0.17f, 0.80f, 0f, 0.16f, 0.20f, 0.05f, WeaponMaterial.Steel, roll: 90f, shape: Spike),
            Box(0f, 0.76f, 0.13f, 0.07f, 0.12f, 0.06f, WeaponMaterial.Steel, pitch: 90f, shape: Spike),
            Box(0f, 0.80f, -0.20f, 0.06f, 0.26f, 0.07f, WeaponMaterial.Steel, pitch: -90f, shape: Spike),
            Box(0f, 0.97f, 0f, 0.07f, 0.14f, 0.07f, WeaponMaterial.Steel, shape: Spike));

        // A long haft and a heavy block head: the head and langet stay rectilinear (a war hammer IS a
        // forged block) but bevelled, with a crowned striking face, a LONG back pick that overhangs the
        // haft, and a lug under the face - the head has to break its own square outline to read as a hammer
        // rather than as a blob, and the pick is what tells the eye which way it is swung.
        Hand("held.weapon.hammer", false,
            Box(0f, 0.45f, 0f, 0.08f, 1.30f, 0.08f, WeaponMaterial.Wood, shape: Shaft),
            Box(0f, -0.20f, 0f, 0.11f, 0.08f, 0.11f, WeaponMaterial.DarkIron, pitch: 180f, shape: Taper),
            Box(0f, 0.05f, 0f, 0.095f, 0.34f, 0.095f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 1.00f, 0f, 0.20f, 0.22f, 0.30f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 1.00f, 0.22f, 0.24f, 0.14f, 0.28f, WeaponMaterial.Steel, pitch: 90f, shape: Dome),
            Box(0f, 0.99f, -0.27f, 0.07f, 0.30f, 0.09f, WeaponMaterial.Steel, pitch: -90f, shape: Spike),
            Box(0f, 0.88f, 0.20f, 0.14f, 0.10f, 0.14f, WeaponMaterial.DarkIron, pitch: 90f, shape: Taper),
            Box(0f, 0.82f, 0f, 0.115f, 0.30f, 0.115f, WeaponMaterial.DarkIron, shape: EquipmentShape.Lame),
            Box(0.11f, 1.00f, 0f, 0.05f, 0.04f, 0.05f, WeaponMaterial.Steel, roll: -90f, shape: Dome),
            Box(-0.11f, 1.00f, 0f, 0.05f, 0.04f, 0.05f, WeaponMaterial.Steel, roll: 90f, shape: Dome));

        // A long shaft, a winged leaf head on langets, a red tassel and an iron butt.
        Hand("held.weapon.spear", false,
            Box(0f, 0.62f, 0f, 0.06f, 2.10f, 0.06f, WeaponMaterial.Wood, shape: Shaft),
            Box(0f, -0.42f, 0f, 0.09f, 0.10f, 0.09f, WeaponMaterial.DarkIron, pitch: 180f, shape: Taper),
            Box(0f, 0.20f, 0f, 0.075f, 0.30f, 0.075f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 1.68f, 0f, 0.10f, 0.14f, 0.03f, WeaponMaterial.Cloth, shape: Panel),
            Box(0f, 1.78f, 0f, 0.09f, 0.20f, 0.09f, WeaponMaterial.DarkIron, shape: EquipmentShape.Lame),
            Box(0f, 1.70f, 0.035f, 0.035f, 0.22f, 0.02f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 1.70f, -0.035f, 0.035f, 0.22f, 0.02f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 1.90f, 0f, 0.05f, 0.24f, 0.15f, WeaponMaterial.Steel, shape: Blade),
            Box(0f, 1.84f, 0.09f, 0.035f, 0.12f, 0.07f, WeaponMaterial.Steel, shape: Crescent),
            Box(0f, 1.84f, -0.09f, 0.035f, 0.12f, 0.07f, WeaponMaterial.Steel, yaw: 180f, shape: Crescent),
            Box(0f, 2.14f, 0f, 0.04f, 0.14f, 0.04f, WeaponMaterial.Steel, shape: Blade));

        // Tapered limbs bent toward the archer (-Z), gilt nocks and a taut string. The lower limb and
        // lower outer limb carry their pitch + 180 so the taper still narrows toward the nock.
        Hand("held.weapon.bow", true,
            Box(0f, 0f, 0f, 0.08f, 0.26f, 0.08f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 0f, 0.02f, 0.06f, 0.34f, 0.06f, WeaponMaterial.Wood, shape: BevelBox),
            Box(0f, 0.30f, -0.03f, 0.06f, 0.36f, 0.06f, WeaponMaterial.Wood, pitch: -14f, shape: Taper),
            Box(0f, -0.30f, -0.03f, 0.06f, 0.36f, 0.06f, WeaponMaterial.Wood, pitch: 194f, shape: Taper),
            Box(0f, 0.62f, -0.13f, 0.055f, 0.34f, 0.055f, WeaponMaterial.Wood, pitch: -32f, shape: Taper),
            Box(0f, -0.62f, -0.13f, 0.055f, 0.34f, 0.055f, WeaponMaterial.Wood, pitch: 212f, shape: Taper),
            Box(0f, 0.80f, -0.24f, 0.07f, 0.07f, 0.07f, WeaponMaterial.Gilt, shape: Dome),
            Box(0f, -0.80f, -0.24f, 0.07f, 0.07f, 0.07f, WeaponMaterial.Gilt, roll: 180f, shape: Dome),
            Box(0f, 0f, -0.24f, 0.015f, 1.58f, 0.015f, WeaponMaterial.Bone, shape: Shaft));

        // A tall dark staff: an ASYMMETRIC claw closing on a glowing stone under a crown ring - one long
        // front talon, two side talons of unequal length and a short rear horn swept back. Four equal
        // talons at the compass points drew as a symmetric lump at actor size and read as a knob.
        Hand("held.weapon.staff", false,
            Box(0f, 0.72f, 0f, 0.07f, 2.00f, 0.07f, WeaponMaterial.Wood, shape: Shaft),
            Box(0f, 0.05f, 0f, 0.09f, 0.34f, 0.09f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 0.24f, 0f, 0.085f, 0.04f, 0.085f, WeaponMaterial.Leather, shape: Ring),
            Box(0f, -0.30f, 0f, 0.09f, 0.08f, 0.09f, WeaponMaterial.DarkIron, pitch: 180f, shape: Taper),
            Box(0f, 1.60f, 0f, 0.05f, 0.10f, 0.05f, WeaponMaterial.DarkIron, pitch: 180f, shape: Spike),
            Box(0f, 1.70f, 0f, 0.13f, 0.08f, 0.13f, WeaponMaterial.DarkIron, shape: Ring),
            Box(0f, 1.88f, 0.10f, 0.04f, 0.40f, 0.04f, WeaponMaterial.DarkIron, pitch: 16f, shape: Taper),
            Box(0.095f, 1.86f, 0.02f, 0.035f, 0.34f, 0.035f, WeaponMaterial.DarkIron, roll: -14f, shape: Taper),
            Box(-0.095f, 1.84f, 0.02f, 0.035f, 0.30f, 0.035f, WeaponMaterial.DarkIron, roll: 14f, shape: Taper),
            Box(0f, 1.78f, -0.11f, 0.05f, 0.22f, 0.05f, WeaponMaterial.DarkIron, pitch: -40f, shape: Spike),
            Box(0f, 1.88f, 0f, 0.13f, 0.20f, 0.13f, WeaponMaterial.Arcane, roll: 45f, shape: Gem),
            Box(0f, 2.01f, 0f, 0.12f, 0.035f, 0.12f, WeaponMaterial.DarkIron, shape: Ring));

        // A plain arming sword: a short broad blade, a gilt drooping guard and a domed bone pommel.
        Hand("held.weapon.short-sword", false,
            Box(0f, 0.08f, 0f, 0.08f, 0.26f, 0.08f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, -0.06f, 0f, 0.10f, 0.08f, 0.10f, WeaponMaterial.Bone, roll: 180f, shape: Dome),
            Box(0f, -0.015f, 0f, 0.085f, 0.022f, 0.085f, WeaponMaterial.Gilt, shape: Ring),
            Box(0f, -0.115f, 0f, 0.05f, 0.05f, 0.05f, WeaponMaterial.Gilt, roll: 180f, shape: Gem),
            Box(0f, 0.23f, 0.075f, 0.045f, 0.145f, 0.045f, WeaponMaterial.Gilt, pitch: 90f, shape: Horn),
            Box(0f, 0.23f, -0.075f, 0.045f, 0.145f, 0.045f, WeaponMaterial.Gilt, pitch: 90f, yaw: 180f, shape: Horn),
            Box(0f, 0.26f, 0f, 0.07f, 0.04f, 0.07f, WeaponMaterial.Gilt, shape: Taper),
            Box(0f, 0.58f, 0f, 0.05f, 0.66f, 0.17f, WeaponMaterial.Steel, shape: Blade),
            Box(0f, 0.54f, 0f, 0.058f, 0.48f, 0.05f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 0.94f, 0f, 0.04f, 0.10f, 0.09f, WeaponMaterial.Steel, shape: Blade));

        // A heavy single-edged falchion: no crossguard, a dark spine, a swept clipped tip.
        Hand("held.weapon.iron-blade", false,
            Box(0f, 0.09f, 0f, 0.08f, 0.28f, 0.08f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, -0.06f, 0f, 0.095f, 0.06f, 0.095f, WeaponMaterial.DarkIron, roll: 180f, shape: Dome),
            Box(0f, 0.25f, 0.085f, 0.055f, 0.17f, 0.055f, WeaponMaterial.DarkIron, pitch: 90f, shape: Horn),
            Box(0f, 0.25f, -0.085f, 0.055f, 0.17f, 0.055f, WeaponMaterial.DarkIron, pitch: 90f, yaw: 180f, shape: Horn),
            Box(0f, 0.68f, 0f, 0.06f, 0.80f, 0.22f, WeaponMaterial.Steel, shape: Blade),
            Box(0f, 0.66f, -0.10f, 0.07f, 0.72f, 0.04f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 0.30f, 0f, 0.065f, 0.10f, 0.09f, WeaponMaterial.DarkIron, shape: Taper),
            Box(0f, 1.12f, 0.03f, 0.05f, 0.18f, 0.14f, WeaponMaterial.Steel, pitch: -18f, shape: Blade));

        // A short haft and a small bearded bit, with a back barb.
        Hand("held.weapon.hand-axe", false,
            Box(0f, 0.165f, 0f, 0.06f, 0.576f, 0.06f, WeaponMaterial.Wood, shape: Shaft),
            Box(0f, -0.132f, 0f, 0.09f, 0.041f, 0.09f, WeaponMaterial.DarkIron, pitch: 180f, shape: Taper),
            Box(0f, 0.03f, 0f, 0.075f, 0.16f, 0.075f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 0.39f, 0f, 0.085f, 0.03f, 0.085f, WeaponMaterial.DarkIron, shape: Ring),
            Box(0f, 0.478f, 0.02f, 0.10f, 0.132f, 0.10f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 0.478f, 0.16f, 0.045f, 0.231f, 0.20f, WeaponMaterial.Steel, shape: Crescent),
            Box(0f, 0.478f, 0.27f, 0.04f, 0.28f, 0.04f, WeaponMaterial.Steel, shape: Crescent),
            Box(0f, 0.478f, -0.07f, 0.04f, 0.10f, 0.05f, WeaponMaterial.DarkIron, pitch: -90f, shape: Spike));

        // A small recurved bow with bone nocks and a short string.
        Hand("held.weapon.short-bow", true,
            Box(0f, 0f, 0f, 0.08f, 0.22f, 0.08f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 0f, 0.015f, 0.05f, 0.28f, 0.05f, WeaponMaterial.Wood, shape: BevelBox),
            Box(0f, 0.24f, -0.02f, 0.05f, 0.30f, 0.05f, WeaponMaterial.Wood, pitch: -12f, shape: Taper),
            Box(0f, -0.24f, -0.02f, 0.05f, 0.30f, 0.05f, WeaponMaterial.Wood, pitch: 192f, shape: Taper),
            Box(0f, 0.46f, -0.10f, 0.05f, 0.26f, 0.05f, WeaponMaterial.Wood, pitch: -30f, shape: Taper),
            Box(0f, -0.46f, -0.10f, 0.05f, 0.26f, 0.05f, WeaponMaterial.Wood, pitch: 210f, shape: Taper),
            Box(0f, 0.60f, -0.19f, 0.06f, 0.06f, 0.06f, WeaponMaterial.Bone, shape: Dome),
            Box(0f, -0.60f, -0.19f, 0.06f, 0.06f, 0.06f, WeaponMaterial.Bone, roll: 180f, shape: Dome),
            Box(0f, 0f, -0.19f, 0.015f, 1.20f, 0.015f, WeaponMaterial.Bone, shape: Shaft));

        // A pale bone shaft: a skull cap with a cold arcane stone above it, vertebra rings, a jutting jaw and
        // two horns of DIFFERENT length and sweep, so the head has a front and a back at actor size.
        Hand("held.weapon.bone-staff", false,
            Box(0f, 0.60f, 0f, 0.08f, 1.70f, 0.08f, WeaponMaterial.Bone, shape: Shaft),
            Box(0f, 0.05f, 0f, 0.10f, 0.30f, 0.10f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, -0.28f, 0f, 0.10f, 0.08f, 0.10f, WeaponMaterial.DarkIron, pitch: 180f, shape: Taper),
            Box(0f, 1.30f, 0f, 0.095f, 0.05f, 0.095f, WeaponMaterial.Bone, shape: Ring),
            Box(0f, 1.55f, 0f, 0.18f, 0.16f, 0.20f, WeaponMaterial.Bone, shape: Dome),
            Box(0f, 1.48f, 0.08f, 0.14f, 0.06f, 0.10f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 1.56f, 0.105f, 0.12f, 0.05f, 0.02f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0.08f, 1.68f, -0.01f, 0.05f, 0.20f, 0.05f, WeaponMaterial.Bone, roll: -26f, shape: Spike),
            Box(-0.075f, 1.64f, 0.01f, 0.045f, 0.13f, 0.045f, WeaponMaterial.Bone, roll: 14f, shape: Spike),
            Box(0f, 1.47f, 0.13f, 0.10f, 0.09f, 0.10f, WeaponMaterial.DarkIron, pitch: 90f, shape: Taper),
            Box(0f, 1.76f, 0f, 0.09f, 0.12f, 0.09f, WeaponMaterial.Arcane, roll: 45f, shape: Gem));

        // A gilt cage lantern with a cold arcane flame, a domed roof and a hanging bail.
        Hand("held.gear.ward-lantern", false,
            Box(0f, 0.35f, 0f, 0.10f, 0.03f, 0.10f, WeaponMaterial.Gilt, pitch: 90f, shape: Ring),
            Box(0f, 0.22f, 0f, 0.03f, 0.26f, 0.03f, WeaponMaterial.DarkIron, shape: Shaft),
            Box(0f, 0.08f, 0f, 0.18f, 0.10f, 0.18f, WeaponMaterial.Gilt, shape: Dome),
            Box(0f, -0.02f, 0f, 0.12f, 0.18f, 0.12f, WeaponMaterial.Arcane, shape: Gem),
            Box(0.075f, -0.02f, 0.075f, 0.025f, 0.22f, 0.025f, WeaponMaterial.Gilt, shape: Shaft),
            Box(-0.075f, -0.02f, 0.075f, 0.025f, 0.22f, 0.025f, WeaponMaterial.Gilt, shape: Shaft),
            Box(0.075f, -0.02f, -0.075f, 0.025f, 0.22f, 0.025f, WeaponMaterial.Gilt, shape: Shaft),
            Box(-0.075f, -0.02f, -0.075f, 0.025f, 0.22f, 0.025f, WeaponMaterial.Gilt, shape: Shaft),
            Box(0f, -0.13f, 0f, 0.18f, 0.04f, 0.18f, WeaponMaterial.Gilt, shape: Ring));

        // Small round board: a bowed wooden disc inside an iron hoop, stepping up through a darker
        // inner dome to a steel boss and a spiked finial - three concentric rises, so the face reads as
        // a curve with a run of light instead of a flat plate.
        Shield("held.shield.buckler",
            Box(0f, 0f, 0f, 0.40f, 0.07f, 0.40f, WeaponMaterial.Wood, pitch: 90f, shape: Dome),
            Box(0f, 0.01f, 0f, 0.44f, 0.05f, 0.44f, WeaponMaterial.DarkIron, pitch: 90f, shape: Ring),
            Box(0f, 0f, 0.03f, 0.22f, 0.06f, 0.22f, WeaponMaterial.DarkIron, pitch: 90f, shape: Dome),
            Box(0f, 0f, 0.07f, 0.14f, 0.06f, 0.14f, WeaponMaterial.Steel, pitch: 90f, shape: Dome),
            Box(0f, 0f, 0.12f, 0.05f, 0.06f, 0.05f, WeaponMaterial.Gilt, pitch: 90f, shape: Spike),
            Box(0f, 0.16f, 0.03f, 0.04f, 0.04f, 0.04f, WeaponMaterial.DarkIron, pitch: 90f, shape: Spike),
            Box(0f, -0.16f, 0.03f, 0.04f, 0.04f, 0.04f, WeaponMaterial.DarkIron, pitch: 90f, shape: Spike),
            Box(0f, 0f, -0.05f, 0.06f, 0.18f, 0.04f, WeaponMaterial.Leather, shape: BevelBox));

        // A heater: ONE Greave is the whole field - wide across the top, tapering to the point, bowed
        // away from the bearer - with a curved steel top band, a red cross that follows the bow, a boss
        // in its own iron ring, and the point tipped in a short taper.
        Shield("held.shield.kite",
            Box(0f, 0.02f, 0f, 0.56f, 0.66f, 0.14f, WeaponMaterial.Wood, shape: EquipmentShape.Greave),
            Box(0f, -0.43f, 0.03f, 0.33f, 0.26f, 0.08f, WeaponMaterial.Wood, pitch: 180f, shape: Taper),
            Box(0f, 0.34f, 0.05f, 0.58f, 0.05f, 0.14f, WeaponMaterial.Steel, shape: Plate),
            Box(0f, 0.04f, 0.075f, 0.07f, 0.74f, 0.03f, WeaponMaterial.Cloth, shape: Panel),
            Box(0f, 0.16f, 0.05f, 0.46f, 0.07f, 0.14f, WeaponMaterial.Cloth, shape: Plate),
            Box(0f, 0.10f, 0.09f, 0.12f, 0.06f, 0.12f, WeaponMaterial.Steel, pitch: 90f, shape: Dome),
            Box(0f, 0.10f, 0.14f, 0.05f, 0.05f, 0.05f, WeaponMaterial.Gilt, pitch: 90f, shape: Spike),
            Box(0f, 0.10f, 0.085f, 0.20f, 0.04f, 0.20f, WeaponMaterial.DarkIron, pitch: 90f, shape: Ring));

        // A door: the planks are a bowed Cuirass shell, so the whole face curves away from the bearer,
        // and the iron over it stays rectilinear - stiles and rails as bevelled bars, the top and bottom
        // rails stepping in a narrower cap, the cross bands bowed to follow the planks.
        Shield("held.shield.tower",
            Box(0f, 0f, 0f, 0.66f, 1.04f, 0.15f, WeaponMaterial.Wood, shape: Plate),
            Box(0f, 0.53f, 0.04f, 0.72f, 0.06f, 0.14f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 0.475f, 0.06f, 0.52f, 0.05f, 0.14f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, -0.53f, 0.04f, 0.72f, 0.06f, 0.14f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, -0.475f, 0.06f, 0.52f, 0.05f, 0.14f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0.33f, 0f, 0.0f, 0.06f, 1.10f, 0.11f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(-0.33f, 0f, 0.0f, 0.06f, 1.10f, 0.11f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 0.20f, 0.04f, 0.68f, 0.06f, 0.16f, WeaponMaterial.DarkIron, shape: Plate),
            Box(0f, -0.20f, 0.04f, 0.68f, 0.06f, 0.16f, WeaponMaterial.DarkIron, shape: Plate),
            Box(0f, 0f, 0.11f, 0.20f, 0.08f, 0.20f, WeaponMaterial.Steel, pitch: 90f, shape: Dome));

        // The older tool visuals.
        Hand("held.tool.blade", false,
            Box(0f, 0.08f, 0f, 0.07f, 0.22f, 0.07f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, -0.04f, 0f, 0.085f, 0.06f, 0.085f, WeaponMaterial.Steel, roll: 180f, shape: Dome),
            Box(0f, 0.20f, 0.070f, 0.042f, 0.135f, 0.042f, WeaponMaterial.Gilt, pitch: 90f, shape: Horn),
            Box(0f, 0.20f, -0.070f, 0.042f, 0.135f, 0.042f, WeaponMaterial.Gilt, pitch: 90f, yaw: 180f, shape: Horn),
            Box(0f, 0.23f, 0f, 0.06f, 0.03f, 0.06f, WeaponMaterial.Gilt, shape: Taper),
            Box(0f, 0.62f, 0f, 0.05f, 0.80f, 0.12f, WeaponMaterial.Steel, shape: Blade),
            Box(0f, 0.58f, 0f, 0.055f, 0.56f, 0.04f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 1.05f, 0f, 0.04f, 0.10f, 0.06f, WeaponMaterial.Steel, shape: Blade));

        Hand("held.tool.maul", false,
            Box(0f, 0.40f, 0f, 0.09f, 1.20f, 0.09f, WeaponMaterial.Wood, shape: Shaft),
            Box(0f, 0f, 0f, 0.105f, 0.26f, 0.105f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 1.05f, 0f, 0.30f, 0.30f, 0.42f, WeaponMaterial.DarkIron, shape: BevelBox),
            Box(0f, 1.05f, 0.22f, 0.32f, 0.06f, 0.34f, WeaponMaterial.Steel, pitch: 90f, shape: Dome),
            Box(0f, 1.05f, -0.22f, 0.32f, 0.06f, 0.34f, WeaponMaterial.Steel, pitch: -90f, shape: Dome),
            Box(0f, 0.85f, 0f, 0.13f, 0.06f, 0.13f, WeaponMaterial.DarkIron, shape: Ring));

        Hand("held.tool.builder", false,
            Box(0f, 0.20f, 0f, 0.07f, 0.55f, 0.07f, WeaponMaterial.Wood, shape: Shaft),
            Box(0f, 0.55f, 0.05f, 0.06f, 0.22f, 0.22f, WeaponMaterial.Steel, shape: BevelBox),
            Box(0f, 0.50f, 0f, 0.09f, 0.08f, 0.09f, WeaponMaterial.DarkIron, shape: Taper),
            Box(0f, -0.09f, 0f, 0.075f, 0.04f, 0.075f, WeaponMaterial.Wood, pitch: 180f, shape: Taper),
            Box(0f, 0.05f, 0f, 0.085f, 0.05f, 0.085f, WeaponMaterial.Leather, shape: Ring));

        Hand("held.tool.torch", false,
            Box(0f, 0.169f, 0f, 0.07f, 0.479f, 0.07f, WeaponMaterial.Wood, shape: Shaft),
            Box(0f, 0.30f, 0f, 0.085f, 0.03f, 0.085f, WeaponMaterial.DarkIron, shape: Ring),
            Box(0f, 0.44f, 0f, 0.12f, 0.09f, 0.12f, WeaponMaterial.Leather, shape: Shaft),
            Box(0f, 0.536f, 0f, 0.13f, 0.102f, 0.13f, WeaponMaterial.DarkIron, shape: Taper),
            Box(0f, 0.604f, 0f, 0.09f, 0.09f, 0.09f, WeaponMaterial.Ember, roll: 45f, shape: Gem),
            Box(0f, 0.666f, 0f, 0.05f, 0.068f, 0.05f, WeaponMaterial.Ember, roll: 45f, shape: Gem));

        Hand("held.tool.lantern", false,
            Box(0f, 0.35f, 0f, 0.09f, 0.03f, 0.09f, WeaponMaterial.DarkIron, pitch: 90f, shape: Ring),
            Box(0f, 0.20f, 0f, 0.025f, 0.30f, 0.025f, WeaponMaterial.DarkIron, shape: Shaft),
            Box(0f, 0.05f, 0f, 0.20f, 0.06f, 0.20f, WeaponMaterial.DarkIron, shape: Dome),
            Box(0f, 0.00f, 0f, 0.14f, 0.16f, 0.14f, WeaponMaterial.Ember, shape: Gem),
            Box(0.085f, 0f, 0f, 0.025f, 0.20f, 0.025f, WeaponMaterial.DarkIron, shape: Shaft),
            Box(-0.085f, 0f, 0f, 0.025f, 0.20f, 0.025f, WeaponMaterial.DarkIron, shape: Shaft),
            Box(0f, -0.10f, 0f, 0.20f, 0.04f, 0.20f, WeaponMaterial.DarkIron, shape: Ring));

        return models;
    }
}
