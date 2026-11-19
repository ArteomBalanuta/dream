using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;

// Palette sampled from the baked figure (tools/ui-assets/gen_char_player.py), not from WeaponMaterial:
// the weapon steel is brighter than the figure's own, and output is albedo x gain with no tonemap.
public enum ArmourMaterial
{
    Steel,
    DarkIron,
    Leather,
    ClothCold,
    ClothAsh,
    ClothRed,
    Bone,
    Gilt,
    Arcane,
    Ember
}

// The body node a worn model rides on. Arms, Legs and Feet are drawn once per side, mirrored in X.
public enum ArmourMount
{
    Head,
    Torso,
    Arms,
    Legs,
    Feet,
    Back,
    Companion
}

// One part of a worn model: centre and size in the mount node's own frame (blocky engine units,
// +Y up, +Z forward, 1 authored u of gen_char_player.py = 0.088), turned about X (Pitch), then Z
// (Roll), then Y (Yaw), in degrees. Worn models draw at scale 1.0. Shape is the sculpted primitive
// the part is drawn with; its mesh is inscribed in the same box the centre and size describe, so
// Size is still the part's real extent whatever the shape.
public readonly record struct ArmourPart(
    Vector3 Center,
    Vector3 Size,
    ArmourMaterial Material,
    float PitchDegrees = 0f,
    float RollDegrees = 0f,
    EquipmentShape Shape = EquipmentShape.Box,
    float YawDegrees = 0f)
{
    // Local box (unit cube) to node space, for row vectors.
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

public sealed class ArmourModel(string visualId, ArmourMount mount, ArmourPart[] parts)
{
    public string VisualId { get; } = visualId;

    public ArmourMount Mount { get; } = mount;

    public IReadOnlyList<ArmourPart> Parts => parts;

    internal ArmourPart[] PartArray => parts;
}

// The procedural meshes of the armour a player wears, by per-item visual id. The base figure is a
// plausible underlayer (tunic, breeches, boots, bare head and hands), so every piece SITS ON the body:
// nothing encloses anything, and the contract in each section says which cover region the piece must
// still cover and how far past the body it may stand (the fit ceiling). A piece that is wider than
// about 1.6x the body at its node reads as a block, not as clothing.
// At the shipped isometric zoom the whole figure is 28x101 pixels, so a piece differs by outline,
// mass and value only; each has 3..10 parts, a light top facet and a deep dark mass, because the parts
// get no bake and no ambient occlusion. An id this catalog does not know draws nothing at all: a
// wrong box on the head is worse than none.
//
// Every part names an EquipmentShape, and none of them is a plain Box: a helm skull or a knee cop is a
// Dome, a pauldron / cuirass / greave / bracer / sleeve is a Plate (a 150 degree shell that WRAPS the
// limb instead of boxing it), a collar or mail row is a Ring, a cloak or tabard or aventail is a Panel,
// a stud or barb is a Spike, a sabaton toe or nasal bar is a Taper, and anything honestly rectilinear
// (a lame, a belt, a buckle, a visor slit) is a BevelBox, whose chamfers catch a light value its faces
// do not. Sizes are authored in the part's OWN frame, before its rotation.
//
// Rotation conventions used below. A Plate opens toward its own -Z, so a sleeve or cheek guard is
// yawed +-90 degrees to put the opening against the body; yaw also swaps which authored axis becomes
// world X and which becomes world Z. A Panel is thin in its own Z, so yaw: 90f hangs it at the side of
// a limb and pitch: 90f lays it flat (a boot sole). A Dome caps its own +Y, so pitch: 90f makes a knee
// cop or a toe cap that bulges forward. A Spike points along its own +Y.
public static class ArmourMeshCatalog
{
    private static readonly List<ArmourModel> Ordered = BuildModels();

    private static readonly Dictionary<string, ArmourModel> Models =
        Ordered.ToDictionary(model => model.VisualId, StringComparer.Ordinal);

    public static IEnumerable<string> KnownVisualIds => Ordered.Select(model => model.VisualId);

    public static bool IsKnown(string? visualId) => visualId != null && Models.ContainsKey(visualId);

    public static ArmourModel? Resolve(string? visualId) =>
        visualId != null && Models.TryGetValue(visualId, out ArmourModel? model) ? model : null;

    // Linear ALBEDO, not final pixel colour. A worn part is drawn by enemyFragment.glsl as
    // (ambient * scale + direct * 0.75) * albedo * exposure, with no tonemap and no gamma encode, so the bytes on
    // screen are this value times a gain that reaches about 2.2 beside a brazier and 1.4 at noon. Anything
    // above ~0.45 in a channel therefore clips to a flat patch, and anything mid-valued reads as a bright block
    // (the old Bone 0.62, ClothRed 0.353 and Ember 0.95 did exactly that). Pieces differ by VALUE and MATERIAL
    // (dark iron < leather < steel < bone, cloth by darkness), never by saturation; every lit channel stays
    // under 0.44 so it survives the strongest light, which tools/ui-assets/tests/check_char.py enforces by
    // rasterising each piece and counting fully-saturated pixels. The glowing materials keep their glow in
    // Emissive() (added after the gain) and a dark body here, so they read as light on a dark piece.
    public static Vector4 BaseColor(ArmourMaterial material) => material switch
    {
        ArmourMaterial.Steel => new Vector4(0.30f, 0.315f, 0.345f, 1f),
        ArmourMaterial.DarkIron => new Vector4(0.115f, 0.115f, 0.13f, 1f),
        ArmourMaterial.Leather => new Vector4(0.13f, 0.08f, 0.06f, 1f),
        ArmourMaterial.ClothCold => new Vector4(0.07f, 0.078f, 0.10f, 1f),
        ArmourMaterial.ClothAsh => new Vector4(0.17f, 0.162f, 0.145f, 1f),
        ArmourMaterial.ClothRed => new Vector4(0.20f, 0.055f, 0.045f, 1f),
        ArmourMaterial.Bone => new Vector4(0.40f, 0.36f, 0.29f, 1f),
        ArmourMaterial.Gilt => new Vector4(0.32f, 0.23f, 0.09f, 1f),
        ArmourMaterial.Arcane => new Vector4(0.10f, 0.06f, 0.17f, 1f),
        ArmourMaterial.Ember => new Vector4(0.24f, 0.10f, 0.04f, 1f),
        _ => new Vector4(0.3f, 0.3f, 0.3f, 1f)
    };

    // Added after lighting: only the glowing materials are not zero.
    public static Vector3 Emissive(ArmourMaterial material) => material switch
    {
        ArmourMaterial.Arcane => new Vector3(0.30f, 0.12f, 0.55f),
        ArmourMaterial.Ember => new Vector3(0.9f, 0.4f, 0.08f),
        _ => Vector3.Zero
    };

    private static ArmourPart Box(
        float x, float y, float z,
        float sx, float sy, float sz,
        ArmourMaterial material,
        float pitch = 0f,
        float roll = 0f,
        EquipmentShape shape = EquipmentShape.Box,
        float yaw = 0f)
    {
        return new ArmourPart(new Vector3(x, y, z), new Vector3(sx, sy, sz), material, pitch, roll, shape, yaw);
    }

    private static List<ArmourModel> BuildModels()
    {
        List<ArmourModel> models = [];

        void Add(string id, ArmourMount mount, params ArmourPart[] parts) => models.Add(new ArmourModel(id, mount, parts));

        const ArmourMaterial Steel = ArmourMaterial.Steel;
        const ArmourMaterial Iron = ArmourMaterial.DarkIron;
        const ArmourMaterial Leather = ArmourMaterial.Leather;
        const ArmourMaterial Cold = ArmourMaterial.ClothCold;
        const ArmourMaterial Ash = ArmourMaterial.ClothAsh;
        const ArmourMaterial Red = ArmourMaterial.ClothRed;
        const ArmourMaterial Bone = ArmourMaterial.Bone;
        const ArmourMaterial Gilt = ArmourMaterial.Gilt;

        const EquipmentShape BevelBox = EquipmentShape.BevelBox;
        const EquipmentShape Shaft = EquipmentShape.Shaft;
        const EquipmentShape Taper = EquipmentShape.Taper;
        const EquipmentShape Plate = EquipmentShape.Plate;
        const EquipmentShape Dome = EquipmentShape.Dome;
        const EquipmentShape Spike = EquipmentShape.Spike;
        const EquipmentShape Ring = EquipmentShape.Ring;
        const EquipmentShape Gem = EquipmentShape.Gem;
        const EquipmentShape Panel = EquipmentShape.Panel;

        // ---- Head: sits on a bare head, y 0..0.42, |x| <= 0.12, |z| <= 0.13. A helm's shell is the head plus
        // a wall (0.04) plus a hand of clearance and its brim: main mass 0.32..0.34 wide (half-width 0.16..0.17,
        // a shell 0.04..0.05 thick, 1.5-2 px at the actor's 0.0265 units per px), brim and rims at most 0.17.
        // Nothing wider than 0.18, which is the head's half-width plus the 0.06 head-shell allowance (1.5x).

        // A soft cap: a domed leather crown, a headband, two hanging ear flaps and a nape flap.
        Add("worn.head.leather-cap", ArmourMount.Head,
            Box(0f, 0.22f, 0f, 0.32f, 0.44f, 0.30f, Leather, shape: Dome),
            Box(0f, 0.12f, -0.16f, 0.32f, 0.26f, 0.06f, Cold, shape: Panel),
            Box(0f, 0.40f, 0f, 0.34f, 0.05f, 0.34f, Ash, shape: Ring),
            Box(0f, 0.49f, 0f, 0.10f, 0.05f, 0.10f, Bone, shape: Dome),
            Box(0f, 0.08f, 0f, 0.33f, 0.06f, 0.33f, Leather, shape: Ring),
            Box(0.155f, 0.10f, 0f, 0.22f, 0.20f, 0.04f, Leather, yaw: 90f, shape: Panel),
            Box(-0.155f, 0.10f, 0f, 0.22f, 0.20f, 0.04f, Leather, yaw: -90f, shape: Panel));

        // An open kettle: a domed skull with a ridged comb, a crown band, wrapping cheek plates, a
        // tapering nasal bar and a mail aventail.
        Add("worn.head.iron-helm", ArmourMount.Head,
            Box(0f, 0.22f, 0f, 0.32f, 0.44f, 0.32f, Iron, shape: Dome),
            Box(0f, 0.46f, 0f, 0.06f, 0.10f, 0.30f, Iron, shape: BevelBox),
            Box(0f, 0.38f, 0f, 0.33f, 0.035f, 0.345f, Steel, shape: Ring),
            Box(0f, 0.22f, 0.17f, 0.05f, 0.26f, 0.04f, Steel, pitch: 180f, shape: Taper),
            Box(0.148f, 0.15f, 0.02f, 0.26f, 0.26f, 0.06f, Iron, yaw: 90f, shape: Plate),
            Box(-0.148f, 0.15f, 0.02f, 0.26f, 0.26f, 0.06f, Iron, yaw: -90f, shape: Plate),
            Box(0f, 0.10f, -0.16f, 0.32f, 0.24f, 0.06f, Cold, shape: Panel),
            Box(0.155f, 0.30f, 0.05f, 0.03f, 0.03f, 0.03f, Steel, roll: -90f, shape: Spike),
            Box(-0.155f, 0.30f, 0.05f, 0.03f, 0.03f, 0.03f, Steel, roll: 90f, shape: Spike));

        // A closed great helm: a tall BLACKENED shell (the skull is the largest up-facing surface on the
        // figure, so it carries the dark material and the steel is only its crown rim), a visor slit and
        // breath plate, a crest spike, gilt brow barbs, a red plume and a gorget ring at the neck.
        Add("worn.head.greathelm", ArmourMount.Head,
            Box(0f, 0.23f, 0f, 0.34f, 0.48f, 0.34f, Iron, shape: Dome),
            Box(0f, 0.355f, 0.175f, 0.30f, 0.07f, 0.03f, Steel, shape: BevelBox),
            Box(0f, 0.14f, 0.18f, 0.14f, 0.16f, 0.03f, Iron, shape: BevelBox),
            Box(0f, 0.49f, 0f, 0.30f, 0.04f, 0.30f, Steel, shape: Ring),
            Box(0f, 0.58f, -0.02f, 0.32f, 0.14f, 0.06f, Red, yaw: 90f, shape: Panel),
            Box(0f, 0.02f, 0f, 0.34f, 0.06f, 0.34f, Iron, shape: Ring),
            Box(0f, 0.60f, 0f, 0.05f, 0.14f, 0.05f, Steel, shape: Spike),
            Box(0.135f, 0.40f, 0.10f, 0.04f, 0.08f, 0.04f, Gilt, roll: -35f, shape: Spike),
            Box(-0.135f, 0.40f, 0.10f, 0.04f, 0.08f, 0.04f, Gilt, roll: 35f, shape: Spike));

        // ---- Body: sits on the tunic. Chest y 0..0.86 to |x| <= 0.19, hip skirt to |x| <= 0.26 (y 0..0.30),
        // z -0.14..+0.14. Main mass 0.42..0.46 wide, pauldrons at |x| 0.26..0.32, nothing past 0.416.

        // A boiled-leather vest: a wrapped body shell, a belted skirt, a chest panel, a small bone gorget and a
        // shoulder yoke CAPPED by a rounded leather pauldron over each shoulder. The camera is pitched 50
        // degrees down, so what the eye reads on a torso is whatever caps the shoulders, not the chest: the
        // yoke plus the caps widen the outline at the shoulders while the belt pulls it in at the waist.
        Add("worn.body.leather-vest", ArmourMount.Torso,
            Box(0f, 0.46f, 0f, 0.46f, 0.80f, 0.34f, Leather, shape: Plate),
            Box(0f, 0.15f, 0f, 0.52f, 0.30f, 0.34f, Ash, shape: Plate),
            Box(0f, 0.50f, 0.165f, 0.16f, 0.50f, 0.03f, Cold, shape: Panel),
            Box(0f, 0.88f, 0f, 0.22f, 0.05f, 0.22f, Bone, shape: Ring),
            Box(0f, 0.79f, 0f, 0.62f, 0.10f, 0.30f, Leather, shape: Plate),
            Box(0f, 0.30f, 0f, 0.54f, 0.07f, 0.36f, Leather, shape: BevelBox),
            Box(0f, 0.30f, 0.19f, 0.08f, 0.08f, 0.04f, Gilt, shape: BevelBox),
            Box(0.27f, 0.82f, 0f, 0.22f, 0.10f, 0.26f, Leather, shape: Dome),
            Box(-0.27f, 0.82f, 0f, 0.22f, 0.10f, 0.26f, Leather, shape: Dome));

        // A mail shirt: a dense even mass of rings in blackened iron, a DOMED cap over each shoulder above the
        // sleeve shells (the mantle used to be one wide near-level steel slab, which is the brightest thing
        // a down-pitched camera can be given), a long skirt, and a small steel gorget as the only bright rim.
        Add("worn.body.chain-hauberk", ArmourMount.Torso,
            Box(0f, 0.46f, 0f, 0.44f, 0.80f, 0.34f, Iron, shape: Plate),
            Box(0.28f, 0.80f, 0f, 0.26f, 0.14f, 0.30f, Iron, shape: Dome),
            Box(-0.28f, 0.80f, 0f, 0.26f, 0.14f, 0.30f, Iron, shape: Dome),
            Box(0f, 0.15f, 0f, 0.52f, 0.30f, 0.34f, Iron, shape: Plate),
            Box(0f, 0.31f, 0f, 0.46f, 0.05f, 0.36f, Cold, shape: Ring),
            Box(0.30f, 0.72f, 0f, 0.26f, 0.22f, 0.14f, Iron, yaw: 90f, shape: Plate),
            Box(-0.30f, 0.72f, 0f, 0.26f, 0.22f, 0.14f, Iron, yaw: -90f, shape: Plate),
            Box(0f, 0.60f, 0f, 0.45f, 0.04f, 0.35f, Iron, shape: Ring),
            Box(0f, 0.86f, 0f, 0.24f, 0.045f, 0.24f, Steel, shape: Ring),
            Box(0f, 0.02f, 0f, 0.50f, 0.05f, 0.34f, Iron, shape: BevelBox));

        // A plate cuirass: a wrapped breastplate (a vertical arc, so the camera sees its glancing front and
        // only the edge of its top rim - that is why the cuirass may stay steel while the shoulders may not)
        // with a RAISED STEEL KEEL RIDGE down the chest, blackened domed pauldron caps with barbs, a dark
        // fauld stepped off the chest by a ring, a small gorget and a red sash.
        Add("worn.body.plate-cuirass", ArmourMount.Torso,
            Box(0f, 0.46f, 0f, 0.46f, 0.80f, 0.34f, Steel, shape: Plate),
            Box(0f, 0.50f, 0.17f, 0.06f, 0.56f, 0.03f, Steel, shape: BevelBox),
            Box(0.30f, 0.80f, 0f, 0.22f, 0.18f, 0.26f, Iron, shape: Dome),
            Box(-0.30f, 0.80f, 0f, 0.22f, 0.18f, 0.26f, Iron, shape: Dome),
            Box(0f, 0.15f, 0f, 0.52f, 0.30f, 0.34f, Iron, shape: Plate),
            Box(0f, 0.33f, 0.17f, 0.46f, 0.06f, 0.03f, Red, shape: Panel),
            Box(0f, 0.88f, 0f, 0.24f, 0.05f, 0.24f, Steel, shape: Ring),
            Box(0.33f, 0.86f, 0f, 0.05f, 0.10f, 0.05f, Steel, roll: -30f, shape: Spike),
            Box(-0.33f, 0.86f, 0f, 0.05f, 0.10f, 0.05f, Steel, roll: 30f, shape: Spike),
            Box(0f, 0.31f, 0f, 0.48f, 0.04f, 0.36f, Iron, shape: Ring));

        // ---- Hands (one arm, centred on the arm's own axis): cover y -0.95..-0.55, |x| <= 0.09, |z| <= 0.09.

        Add("worn.hands.leather-gloves", ArmourMount.Arms,
            Box(0f, -0.76f, 0f, 0.24f, 0.44f, 0.24f, Leather, shape: Plate),
            Box(0f, -0.58f, 0f, 0.28f, 0.10f, 0.28f, Ash, shape: Ring),
            Box(0f, -0.52f, 0f, 0.20f, 0.03f, 0.20f, Bone, shape: Ring),
            Box(0f, -0.94f, 0f, 0.22f, 0.08f, 0.26f, Cold, shape: BevelBox),
            Box(0f, -0.70f, 0.11f, 0.10f, 0.20f, 0.03f, Leather, shape: Panel),
            Box(0.07f, -0.92f, 0.11f, 0.04f, 0.05f, 0.04f, Bone, pitch: 90f, shape: Spike),
            Box(-0.07f, -0.92f, 0.11f, 0.04f, 0.05f, 0.04f, Bone, pitch: 90f, shape: Spike));

        Add("worn.hands.iron-gauntlets", ArmourMount.Arms,
            Box(0f, -0.76f, 0f, 0.26f, 0.44f, 0.26f, Iron, shape: Plate),
            Box(0f, -0.58f, 0f, 0.34f, 0.12f, 0.34f, Iron, shape: Plate),
            Box(0f, -0.51f, 0f, 0.26f, 0.035f, 0.26f, Steel, shape: Ring),
            Box(0f, -0.92f, 0.08f, 0.24f, 0.10f, 0.14f, Steel, shape: BevelBox),
            Box(0f, -0.66f, 0f, 0.28f, 0.05f, 0.28f, Iron, shape: BevelBox),
            Box(0f, -0.74f, 0f, 0.27f, 0.05f, 0.27f, Iron, shape: BevelBox),
            Box(0.07f, -0.95f, 0.10f, 0.04f, 0.07f, 0.04f, Steel, pitch: 90f, shape: Spike),
            Box(-0.07f, -0.95f, 0.10f, 0.04f, 0.07f, 0.04f, Steel, pitch: 90f, shape: Spike));

        Add("worn.hands.plate-gauntlets", ArmourMount.Arms,
            Box(0f, -0.74f, 0f, 0.28f, 0.48f, 0.28f, Steel, shape: Plate),
            Box(0f, -0.80f, 0f, 0.32f, 0.06f, 0.32f, Iron, shape: BevelBox),
            Box(0f, -0.54f, 0f, 0.40f, 0.14f, 0.40f, Iron, shape: Plate),
            Box(0f, -0.46f, 0f, 0.38f, 0.05f, 0.38f, Iron, shape: Ring),
            Box(0f, -0.60f, 0f, 0.28f, 0.04f, 0.28f, Steel, shape: Ring),
            Box(0f, -0.95f, 0.06f, 0.22f, 0.08f, 0.20f, Iron, shape: BevelBox),
            Box(0f, -0.88f, 0.08f, 0.20f, 0.05f, 0.18f, Steel, shape: BevelBox),
            Box(0.08f, -0.98f, 0.08f, 0.04f, 0.08f, 0.04f, Steel, pitch: 90f, shape: Spike),
            Box(-0.08f, -0.98f, 0.08f, 0.04f, 0.08f, 0.04f, Steel, pitch: 90f, shape: Spike));

        // ---- Legs (one leg): cover y -0.75..-0.10, |x| <= 0.11, |z| <= 0.11.
        //
        // A Ring lies in the XZ plane, so under a camera pitched 50 degrees down it presents its WHOLE TOP
        // FACE as a bright ellipse. Several light Rings down a leg stacked into what read as bandages, and
        // the legs are the lowest, nearest part of the figure, so they got the most of it. The rule now:
        // at most ONE light rim per leg piece (a knee or ankle accent, in Steel, kept narrow), every other
        // band is DarkIron or stops being a Ring and becomes a Taper lame that shows its SIDE instead.

        Add("worn.legs.leather-leggings", ArmourMount.Legs,
            Box(0f, -0.43f, 0f, 0.30f, 0.68f, 0.30f, Leather, shape: Plate),
            Box(0f, -0.20f, 0f, 0.32f, 0.10f, 0.32f, Cold, shape: Ring),
            Box(0f, -0.45f, 0.14f, 0.16f, 0.08f, 0.16f, Iron, pitch: 90f, shape: Dome),
            Box(0f, -0.62f, 0f, 0.31f, 0.10f, 0.31f, Iron, pitch: 180f, shape: Taper),
            Box(0f, -0.50f, 0.15f, 0.04f, 0.30f, 0.03f, Leather, shape: Panel),
            Box(0f, -0.70f, 0f, 0.31f, 0.05f, 0.31f, Leather, shape: Ring),
            Box(0f, -0.555f, 0.16f, 0.07f, 0.05f, 0.05f, Steel, shape: BevelBox));

        Add("worn.legs.plated-leggings", ArmourMount.Legs,
            Box(0f, -0.43f, 0f, 0.30f, 0.68f, 0.30f, Cold, shape: Plate),
            Box(0f, -0.26f, 0f, 0.34f, 0.30f, 0.34f, Iron, shape: Plate),
            Box(0f, -0.60f, 0.06f, 0.30f, 0.14f, 0.28f, Iron, shape: Plate),
            Box(0f, -0.70f, 0.04f, 0.28f, 0.12f, 0.30f, Iron, pitch: 180f, shape: Taper),
            Box(0f, -0.45f, 0.15f, 0.18f, 0.08f, 0.18f, Steel, pitch: 90f, shape: Dome),
            Box(0f, -0.52f, 0f, 0.33f, 0.04f, 0.33f, Iron, shape: Ring),
            Box(0f, -0.40f, 0.17f, 0.05f, 0.07f, 0.05f, Steel, pitch: 90f, shape: Spike),
            Box(0f, -0.345f, 0.13f, 0.18f, 0.03f, 0.14f, Steel, shape: Ring));

        Add("worn.legs.chain-chausses", ArmourMount.Legs,
            Box(0f, -0.43f, 0f, 0.32f, 0.68f, 0.32f, Iron, shape: Plate),
            Box(0f, -0.14f, 0f, 0.36f, 0.08f, 0.36f, Iron, shape: Ring),
            Box(0f, -0.73f, 0f, 0.26f, 0.04f, 0.26f, Steel, shape: Ring),
            Box(0f, -0.43f, 0.17f, 0.12f, 0.50f, 0.03f, Ash, shape: Panel),
            Box(0f, -0.30f, 0f, 0.34f, 0.04f, 0.34f, Iron, shape: Ring),
            Box(0f, -0.56f, 0f, 0.33f, 0.10f, 0.33f, Iron, pitch: 180f, shape: Taper),
            Box(0f, -0.45f, 0.15f, 0.16f, 0.06f, 0.16f, Steel, pitch: 90f, shape: Dome));

        Add("worn.legs.plate-greaves", ArmourMount.Legs,
            Box(0f, -0.50f, 0.03f, 0.36f, 0.50f, 0.36f, Iron, shape: Plate),
            Box(0f, -0.20f, 0f, 0.30f, 0.28f, 0.30f, Iron, shape: Plate),
            Box(0f, -0.42f, 0.16f, 0.20f, 0.10f, 0.20f, Steel, pitch: 90f, shape: Dome),
            Box(0f, -0.58f, 0.18f, 0.05f, 0.26f, 0.04f, Steel, shape: BevelBox),
            Box(0f, -0.70f, 0f, 0.26f, 0.04f, 0.26f, Steel, shape: Ring),
            Box(0f, -0.36f, 0.18f, 0.05f, 0.08f, 0.05f, Steel, pitch: 90f, shape: Spike),
            Box(0f, -0.74f, 0.08f, 0.30f, 0.05f, 0.26f, Iron, shape: BevelBox));

        // ---- Feet (one leg): cover y -1.30..-0.88, |x| <= 0.132, z -0.05..+0.15.
        // The same rule as the legs: the boot's own bands used to stack on top of the leg bands into one
        // pale column, so a boot gets a single small light fitting (a buckle, a toe cap) and dark bands.

        Add("worn.feet.leather-boots", ArmourMount.Feet,
            Box(0f, -1.09f, 0.07f, 0.28f, 0.44f, 0.34f, Leather, shape: Plate),
            Box(0f, -0.88f, 0f, 0.32f, 0.06f, 0.30f, Ash, shape: Ring),
            Box(0f, -0.855f, 0.17f, 0.08f, 0.05f, 0.06f, Steel, shape: BevelBox),
            Box(0f, -1.26f, 0.22f, 0.24f, 0.10f, 0.18f, Leather, pitch: 90f, shape: Dome),
            Box(0f, -1.30f, 0.08f, 0.28f, 0.05f, 0.40f, Ash, shape: BevelBox),
            Box(0f, -1.00f, 0.04f, 0.30f, 0.05f, 0.32f, Leather, shape: Ring));

        Add("worn.feet.iron-boots", ArmourMount.Feet,
            Box(0f, -1.09f, 0.07f, 0.30f, 0.44f, 0.34f, Iron, shape: Plate),
            Box(0f, -1.24f, 0.20f, 0.28f, 0.14f, 0.18f, Steel, pitch: 90f, shape: Dome),
            Box(0f, -0.88f, 0.03f, 0.34f, 0.08f, 0.34f, Iron, shape: Plate),
            Box(0f, -1.32f, 0.08f, 0.30f, 0.40f, 0.04f, Leather, pitch: 90f, shape: Panel),
            Box(0f, -0.96f, 0.19f, 0.09f, 0.06f, 0.07f, Steel, shape: BevelBox),
            Box(0f, -1.16f, -0.09f, 0.05f, 0.10f, 0.05f, Iron, pitch: -90f, shape: Spike),
            Box(0f, -1.04f, 0.05f, 0.31f, 0.05f, 0.31f, Iron, shape: Ring));

        Add("worn.feet.plate-sabatons", ArmourMount.Feet,
            Box(0f, -1.08f, 0.07f, 0.30f, 0.42f, 0.36f, Iron, shape: Plate),
            Box(0f, -1.31f, 0.12f, 0.30f, 0.05f, 0.46f, Iron, shape: BevelBox),
            Box(0f, -1.22f, 0.30f, 0.20f, 0.20f, 0.10f, Steel, pitch: 90f, shape: Taper),
            Box(0f, -0.98f, 0.03f, 0.34f, 0.06f, 0.36f, Iron, shape: BevelBox),
            Box(0f, -0.88f, 0.04f, 0.38f, 0.08f, 0.30f, Iron, shape: Plate),
            Box(0f, -1.16f, 0.05f, 0.32f, 0.05f, 0.38f, Iron, shape: BevelBox),
            Box(0f, -0.84f, 0.02f, 0.24f, 0.04f, 0.22f, Steel, shape: Ring),
            Box(0f, -1.20f, 0.38f, 0.05f, 0.07f, 0.05f, Steel, pitch: 90f, shape: Spike));

        // ---- Back (torso frame): the torso back surface is at z >= -0.14 and the panels hang off it at
        // z -0.18..-0.30. Above y 0.30 nothing is wider than 1.6x the torso (0.416); the hem may flare to 0.50.

        // Every cloak is a TAPERED STACK of Panels, never one plank: narrow at the shoulders (<= 0.44), widest
        // near the hem, each tier 0.03 further back (half the 0.06 thickness, so neighbouring tiers do not
        // interpenetrate) and pitched a little more than the one above (positive pitch swings the bottom edge
        // backwards), so the lengths take different light and the hem hangs away from the body. Hem panels are
        // inset or split into tabs so the bottom outline is not a square line. A Panel is itself curved and
        // wavy, which is where cloth now gets its run of light; the clasps and collars are hard shapes against it.

        // Short and ragged: a leather yoke with a clasp, a narrow upper back and FOUR falls hung in pairs,
        // each yawed away from the next so no two take the same light. A single wide tier (this one was
        // 0.70 across) reads as a plank nailed to the figure however well it is pitched: the cure is to
        // split it, turn the halves, and let daylight between them.
        Add("worn.back.travel-cloak", ArmourMount.Back,
            Box(0f, 0.84f, -0.19f, 0.38f, 0.10f, 0.06f, Leather, shape: Panel),
            Box(0f, 0.74f, -0.19f, 0.30f, 0.05f, 0.06f, Leather, shape: BevelBox),
            Box(0f, 0.52f, -0.22f, 0.34f, 0.44f, 0.06f, Ash, pitch: 3f, shape: Panel),
            Box(0.19f, 0.10f, -0.25f, 0.26f, 0.44f, 0.06f, Ash, pitch: 6f, yaw: 18f, shape: Panel),
            Box(-0.19f, 0.10f, -0.25f, 0.26f, 0.44f, 0.06f, Cold, pitch: 6f, yaw: -18f, shape: Panel),
            Box(0.145f, -0.30f, -0.28f, 0.22f, 0.34f, 0.06f, Ash, pitch: 10f, yaw: 12f, shape: Panel),
            Box(-0.145f, -0.26f, -0.28f, 0.22f, 0.26f, 0.06f, Cold, pitch: 10f, yaw: -12f, shape: Panel),
            Box(0f, 0.90f, -0.17f, 0.10f, 0.04f, 0.08f, Steel, shape: BevelBox),
            Box(0f, 0.90f, -0.14f, 0.07f, 0.07f, 0.07f, Gilt, pitch: 90f, shape: Ring));

        // Long, to the ankle, but hung as SIX narrow lengths in mirrored pairs rather than as three broad
        // tiers: the wide tiers (0.56 and 0.70 across) buried the legs in one flat dark slab that had no
        // curvature to show. Each pair is yawed apart, each length of a pair is cut to a different height,
        // so the hem steps instead of running straight across and the legs show between the falls.
        // Above it all a raised standing collar (not a hood) with a bone rim, and a clasp bar at the throat.
        Add("worn.back.hooded-cloak", ArmourMount.Back,
            Box(0f, 0.60f, -0.19f, 0.34f, 0.68f, 0.06f, Ash, pitch: 2f, shape: Panel),
            Box(0.17f, -0.02f, -0.22f, 0.30f, 0.52f, 0.06f, Cold, pitch: 5f, yaw: 16f, shape: Panel),
            Box(-0.17f, -0.02f, -0.22f, 0.30f, 0.52f, 0.06f, Ash, pitch: 5f, yaw: -16f, shape: Panel),
            Box(0.135f, -0.56f, -0.25f, 0.24f, 0.52f, 0.06f, Ash, pitch: 8f, yaw: 12f, shape: Panel),
            Box(-0.135f, -0.68f, -0.25f, 0.24f, 0.44f, 0.06f, Cold, pitch: 8f, yaw: -12f, shape: Panel),
            Box(0.10f, -0.94f, -0.28f, 0.20f, 0.40f, 0.06f, Ash, pitch: 12f, yaw: 8f, shape: Panel),
            Box(-0.10f, -0.90f, -0.28f, 0.20f, 0.34f, 0.06f, Cold, pitch: 12f, yaw: -8f, shape: Panel),
            Box(0f, 1.00f, -0.19f, 0.34f, 0.30f, 0.06f, Ash, pitch: -10f, shape: Panel),
            Box(0f, 1.175f, -0.19f, 0.24f, 0.03f, 0.06f, Bone, shape: Ring),
            Box(0f, 0.86f, -0.17f, 0.26f, 0.05f, 0.07f, Iron, shape: BevelBox));

        // A broad shoulder mantle, layered and drooping at the points with iron barbs, over a short tapered
        // back panel.
        Add("worn.back.grave-mantle", ArmourMount.Back,
            Box(0f, 0.84f, -0.08f, 0.50f, 0.10f, 0.40f, Cold, shape: Plate),
            Box(0.28f, 0.80f, -0.08f, 0.24f, 0.10f, 0.40f, Cold, roll: -18f, shape: Panel),
            Box(-0.28f, 0.80f, -0.08f, 0.24f, 0.10f, 0.40f, Cold, roll: 18f, shape: Panel),
            Box(0f, 0.66f, -0.10f, 0.66f, 0.10f, 0.36f, Ash, shape: Panel),
            Box(0f, 0.905f, -0.08f, 0.26f, 0.03f, 0.22f, Bone, shape: Ring),
            Box(0f, 0.30f, -0.19f, 0.36f, 0.56f, 0.06f, Cold, pitch: 3f, shape: Panel),
            Box(-0.17f, -0.29f, -0.22f, 0.30f, 0.62f, 0.06f, Ash, pitch: 6f, yaw: -14f, shape: Panel),
            Box(0.18f, -0.24f, -0.25f, 0.32f, 0.52f, 0.06f, Cold, pitch: 8f, yaw: 14f, shape: Panel),
            Box(0.34f, 0.74f, -0.06f, 0.07f, 0.07f, 0.07f, Iron, roll: -40f, shape: Spike),
            Box(-0.34f, 0.74f, -0.06f, 0.07f, 0.07f, 0.07f, Iron, roll: 40f, shape: Spike));

        // Not cloth, and not two flat violet quads either: a DARK IRON SPAR FRAME - a long leading spar laid
        // along the top of each wing and a shorter rib under it - carrying a smaller arcane membrane inside.
        // The membrane is the smaller area, the frame is what gives the wing an outline, and the two wings
        // are yawed apart (not merely rolled) so they face different ways and take visibly different light
        // instead of reading as one saturated tone. Bone is kept for the barbed tips only.
        Add("worn.back.gossamer-wings", ArmourMount.Back,
            Box(0.345f, 0.705f, -0.26f, 0.26f, 0.42f, 0.02f, ArmourMaterial.Arcane, roll: -14f, yaw: 20f, shape: Panel),
            Box(-0.345f, 0.705f, -0.26f, 0.26f, 0.42f, 0.02f, ArmourMaterial.Arcane, roll: 14f, yaw: -20f, shape: Panel),
            Box(0.33f, 0.88f, -0.27f, 0.05f, 0.46f, 0.04f, Iron, roll: -70f, shape: Shaft),
            Box(-0.33f, 0.88f, -0.27f, 0.05f, 0.46f, 0.04f, Iron, roll: 70f, shape: Shaft),
            Box(0.30f, 0.62f, -0.26f, 0.04f, 0.40f, 0.04f, Iron, roll: -40f, shape: Shaft),
            Box(-0.30f, 0.62f, -0.26f, 0.04f, 0.40f, 0.04f, Iron, roll: 40f, shape: Shaft),
            Box(0f, 0.62f, -0.24f, 0.20f, 0.20f, 0.06f, Cold, shape: Plate),
            Box(0.46f, 0.94f, -0.27f, 0.05f, 0.08f, 0.05f, Bone, roll: -20f, shape: Spike),
            Box(-0.46f, 0.94f, -0.27f, 0.05f, 0.08f, 0.05f, Bone, roll: 20f, shape: Spike));

        // ---- Companion (torso frame): a free-floating wisp, no envelope.
        Add("worn.companion.ember-wisp", ArmourMount.Companion,
            Box(0.42f, 0.95f, -0.10f, 0.12f, 0.12f, 0.12f, ArmourMaterial.Ember, shape: Gem),
            Box(0.42f, 0.95f, -0.10f, 0.12f, 0.12f, 0.12f, ArmourMaterial.Ember, pitch: 45f, roll: 45f, shape: Gem),
            Box(0.42f, 0.80f, -0.14f, 0.05f, 0.12f, 0.05f, ArmourMaterial.Ember, shape: Taper),
            Box(0.42f, 0.84f, -0.10f, 0.16f, 0.03f, 0.16f, Iron, shape: Ring),
            Box(0.42f, 1.03f, -0.10f, 0.05f, 0.06f, 0.05f, ArmourMaterial.Ember, shape: Spike));

        return models;
    }
}
