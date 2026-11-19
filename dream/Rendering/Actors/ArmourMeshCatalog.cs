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

// One box of a worn model: centre and size in the mount node's own frame (blocky engine units,
// +Y up, +Z forward, 1 authored u of gen_char_player.py = 0.088), turned about X (Pitch) and then
// Z (Roll), in degrees. Worn models draw at scale 1.0.
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
// mass and value only; each has 3..8 boxes, a light top facet and a deep dark mass, because boxes
// get no bake and no ambient occlusion. An id this catalog does not know draws nothing at all: a
// wrong box on the head is worse than none.
public static class ArmourMeshCatalog
{
    private static readonly List<ArmourModel> Ordered = BuildModels();

    private static readonly Dictionary<string, ArmourModel> Models =
        Ordered.ToDictionary(model => model.VisualId, StringComparer.Ordinal);

    public static IEnumerable<string> KnownVisualIds => Ordered.Select(model => model.VisualId);

    public static bool IsKnown(string? visualId) => visualId != null && Models.ContainsKey(visualId);

    public static ArmourModel? Resolve(string? visualId) =>
        visualId != null && Models.TryGetValue(visualId, out ArmourModel? model) ? model : null;

    // Linear ALBEDO, not final pixel colour. A worn box is drawn by enemyFragment.glsl as
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

        // ---- Head: sits on a bare head, y 0..0.42, |x| <= 0.12, |z| <= 0.13. A helm's shell is the head plus
        // a wall (0.04) plus a hand of clearance and its brim: main mass 0.32..0.34 wide (half-width 0.16..0.17,
        // a shell 0.04..0.05 thick, 1.5-2 px at the actor's 0.0265 units per px), brim and rims at most 0.17.
        // Nothing wider than 0.18, which is the head's half-width plus the 0.06 head-shell allowance (1.5x).

        // A soft cap: a dark crown, a brim, a hanging nape flap and a bone button.
        Add("worn.head.leather-cap", ArmourMount.Head,
            Box(0f, 0.22f, 0f, 0.32f, 0.44f, 0.30f, Leather),
            Box(0f, 0.12f, -0.16f, 0.32f, 0.26f, 0.06f, Cold),
            Box(0f, 0.40f, 0f, 0.34f, 0.05f, 0.34f, Ash),
            Box(0f, 0.49f, 0f, 0.10f, 0.05f, 0.10f, Bone));

        // An open kettle: a steel crown and brim, cheek guards, a nasal bar and a mail aventail.
        Add("worn.head.iron-helm", ArmourMount.Head,
            Box(0f, 0.22f, 0f, 0.32f, 0.44f, 0.32f, Iron),
            Box(0f, 0.46f, 0f, 0.28f, 0.08f, 0.28f, Steel),
            Box(0f, 0.38f, 0f, 0.34f, 0.04f, 0.38f, Steel),
            Box(0f, 0.22f, 0.17f, 0.05f, 0.26f, 0.04f, Steel),
            Box(0.15f, 0.15f, 0.02f, 0.04f, 0.26f, 0.26f, Iron),
            Box(-0.15f, 0.15f, 0.02f, 0.04f, 0.26f, 0.26f, Iron),
            Box(0f, 0.10f, -0.16f, 0.32f, 0.24f, 0.06f, Cold));

        // A closed bucket helm: tall, a visor band, a breath plate and a red plume.
        Add("worn.head.greathelm", ArmourMount.Head,
            Box(0f, 0.23f, 0f, 0.34f, 0.48f, 0.34f, Steel),
            Box(0f, 0.34f, 0.18f, 0.28f, 0.05f, 0.03f, Iron),
            Box(0f, 0.14f, 0.18f, 0.14f, 0.16f, 0.03f, Iron),
            Box(0f, 0.49f, 0f, 0.32f, 0.04f, 0.30f, Steel),
            Box(0f, 0.58f, -0.02f, 0.06f, 0.14f, 0.32f, Red),
            Box(0f, 0.02f, 0f, 0.34f, 0.06f, 0.34f, Iron));

        // ---- Body: sits on the tunic. Chest y 0..0.86 to |x| <= 0.19, hip skirt to |x| <= 0.26 (y 0..0.30),
        // z -0.14..+0.14. Main mass 0.42..0.46 wide, pauldrons at |x| 0.26..0.32, nothing past 0.416.

        // A boiled-leather vest: a dark body, a belted skirt, a chest panel, a bone collar and a shoulder yoke that
        // carries the outline past the body (a piece that does not widen the figure fails the fit floor).
        Add("worn.body.leather-vest", ArmourMount.Torso,
            Box(0f, 0.46f, 0f, 0.46f, 0.80f, 0.32f, Leather),
            Box(0f, 0.15f, 0f, 0.52f, 0.30f, 0.34f, Ash),
            Box(0f, 0.50f, 0.165f, 0.16f, 0.50f, 0.03f, Cold),
            Box(0f, 0.88f, 0f, 0.30f, 0.05f, 0.24f, Bone),
            Box(0f, 0.79f, 0f, 0.62f, 0.10f, 0.30f, Leather));

        // A mail shirt: dark mesh, a steel mantle over the shoulders, a long skirt and sleeve stubs.
        Add("worn.body.chain-hauberk", ArmourMount.Torso,
            Box(0f, 0.46f, 0f, 0.44f, 0.80f, 0.32f, Iron),
            Box(0f, 0.82f, 0f, 0.54f, 0.07f, 0.34f, Steel),
            Box(0f, 0.15f, 0f, 0.52f, 0.30f, 0.34f, Steel),
            Box(0f, 0.31f, 0f, 0.46f, 0.05f, 0.34f, Cold),
            Box(0.30f, 0.72f, 0f, 0.14f, 0.22f, 0.26f, Iron),
            Box(-0.30f, 0.72f, 0f, 0.14f, 0.22f, 0.26f, Iron));

        // A plate cuirass: a ridge, big pauldrons, a dark fauld and a red sash.
        Add("worn.body.plate-cuirass", ArmourMount.Torso,
            Box(0f, 0.46f, 0f, 0.46f, 0.80f, 0.32f, Steel),
            Box(0f, 0.50f, 0.17f, 0.06f, 0.56f, 0.03f, Iron),
            Box(0.30f, 0.80f, 0f, 0.22f, 0.16f, 0.30f, Steel),
            Box(-0.30f, 0.80f, 0f, 0.22f, 0.16f, 0.30f, Steel),
            Box(0f, 0.15f, 0f, 0.52f, 0.30f, 0.34f, Iron),
            Box(0f, 0.33f, 0.17f, 0.46f, 0.06f, 0.03f, Red));

        // ---- Hands (one arm, centred on the arm's own axis): cover y -0.95..-0.55, |x| <= 0.09, |z| <= 0.09.

        Add("worn.hands.leather-gloves", ArmourMount.Arms,
            Box(0f, -0.76f, 0f, 0.24f, 0.44f, 0.24f, Leather),
            Box(0f, -0.58f, 0f, 0.28f, 0.10f, 0.28f, Ash),
            Box(0f, -0.52f, 0f, 0.20f, 0.03f, 0.20f, Bone),
            Box(0f, -0.94f, 0f, 0.22f, 0.08f, 0.26f, Cold));

        Add("worn.hands.iron-gauntlets", ArmourMount.Arms,
            Box(0f, -0.76f, 0f, 0.26f, 0.44f, 0.26f, Iron),
            Box(0f, -0.58f, 0f, 0.34f, 0.12f, 0.34f, Steel),
            Box(0f, -0.92f, 0.08f, 0.24f, 0.10f, 0.14f, Steel));

        Add("worn.hands.plate-gauntlets", ArmourMount.Arms,
            Box(0f, -0.74f, 0f, 0.28f, 0.48f, 0.28f, Steel),
            Box(0f, -0.80f, 0f, 0.32f, 0.06f, 0.32f, Iron),
            Box(0f, -0.54f, 0f, 0.40f, 0.14f, 0.40f, Steel),
            Box(0f, -0.95f, 0.06f, 0.22f, 0.08f, 0.20f, Iron));

        // ---- Legs (one leg): cover y -0.75..-0.10, |x| <= 0.11, |z| <= 0.11.

        Add("worn.legs.leather-leggings", ArmourMount.Legs,
            Box(0f, -0.43f, 0f, 0.30f, 0.68f, 0.30f, Leather),
            Box(0f, -0.20f, 0f, 0.32f, 0.10f, 0.32f, Cold),
            Box(0f, -0.62f, 0f, 0.33f, 0.04f, 0.33f, Bone));

        Add("worn.legs.plated-leggings", ArmourMount.Legs,
            Box(0f, -0.43f, 0f, 0.30f, 0.68f, 0.30f, Cold),
            Box(0f, -0.26f, 0f, 0.34f, 0.30f, 0.34f, Steel),
            Box(0f, -0.60f, 0.06f, 0.30f, 0.14f, 0.28f, Steel),
            Box(0f, -0.70f, 0.04f, 0.28f, 0.12f, 0.30f, Iron));

        Add("worn.legs.chain-chausses", ArmourMount.Legs,
            Box(0f, -0.43f, 0f, 0.32f, 0.68f, 0.32f, Iron),
            Box(0f, -0.14f, 0f, 0.36f, 0.08f, 0.36f, Steel),
            Box(0f, -0.73f, 0f, 0.34f, 0.05f, 0.34f, Steel),
            Box(0f, -0.43f, 0.17f, 0.12f, 0.50f, 0.03f, Ash));

        Add("worn.legs.plate-greaves", ArmourMount.Legs,
            Box(0f, -0.50f, 0.03f, 0.36f, 0.50f, 0.36f, Steel),
            Box(0f, -0.20f, 0f, 0.30f, 0.28f, 0.30f, Iron),
            Box(0f, -0.58f, 0.15f, 0.30f, 0.18f, 0.14f, Steel));

        // ---- Feet (one leg): cover y -1.30..-0.88, |x| <= 0.132, z -0.05..+0.15.

        Add("worn.feet.leather-boots", ArmourMount.Feet,
            Box(0f, -1.09f, 0.07f, 0.28f, 0.44f, 0.34f, Leather),
            Box(0f, -0.88f, 0f, 0.32f, 0.06f, 0.30f, Ash),
            Box(0f, -0.85f, 0f, 0.32f, 0.04f, 0.30f, Bone));

        Add("worn.feet.iron-boots", ArmourMount.Feet,
            Box(0f, -1.09f, 0.07f, 0.30f, 0.44f, 0.34f, Iron),
            Box(0f, -1.24f, 0.20f, 0.28f, 0.12f, 0.18f, Steel),
            Box(0f, -0.88f, 0.03f, 0.34f, 0.08f, 0.34f, Steel),
            Box(0f, -1.32f, 0.08f, 0.30f, 0.04f, 0.40f, Leather));

        Add("worn.feet.plate-sabatons", ArmourMount.Feet,
            Box(0f, -1.08f, 0.07f, 0.30f, 0.42f, 0.36f, Steel),
            Box(0f, -1.31f, 0.12f, 0.30f, 0.05f, 0.46f, Iron),
            Box(0f, -1.22f, 0.30f, 0.20f, 0.10f, 0.22f, Steel),
            Box(0f, -0.98f, 0.03f, 0.34f, 0.06f, 0.36f, Iron),
            Box(0f, -0.88f, 0.04f, 0.38f, 0.08f, 0.30f, Steel));

        // ---- Back (torso frame): the torso back surface is at z >= -0.14 and the panels hang off it at
        // z -0.18..-0.30. Above y 0.30 nothing is wider than 1.6x the torso (0.416); the hem may flare to 0.50.

        // Every cloak is a TAPERED STACK, never one plank: narrow at the shoulders (<= 0.44), widest near the
        // hem, each tier 0.03 further back (half the 0.06 thickness, so neighbouring tiers do not interpenetrate) and pitched a little more than the one above (positive pitch swings
        // the bottom edge backwards), so the lengths take different light and the hem hangs away from
        // the body. Hem boxes are inset or split into tabs so the bottom outline is not a square line.

        // Short and ragged: a leather yoke, a stepped body and three tabs of different lengths.
        Add("worn.back.travel-cloak", ArmourMount.Back,
            Box(0f, 0.84f, -0.19f, 0.38f, 0.10f, 0.06f, Leather),
            Box(0f, 0.50f, -0.22f, 0.40f, 0.50f, 0.06f, Ash, pitch: 2f),
            Box(0f, 0.02f, -0.25f, 0.70f, 0.50f, 0.06f, Ash, pitch: 5f),
            Box(0f, -0.30f, -0.28f, 0.24f, 0.36f, 0.06f, Ash, pitch: 9f),
            Box(0.29f, -0.33f, -0.28f, 0.22f, 0.44f, 0.06f, Ash, pitch: 8f),
            Box(-0.29f, -0.27f, -0.28f, 0.22f, 0.32f, 0.06f, Ash, pitch: 8f),
            Box(0f, 0.90f, -0.17f, 0.10f, 0.04f, 0.08f, Steel));

        // Long, to the calf: a stepped trapezoid, a hem of three folds of unequal length and a raised standing collar (not a hood).
        Add("worn.back.hooded-cloak", ArmourMount.Back,
            Box(0f, 0.60f, -0.19f, 0.40f, 0.72f, 0.06f, Ash, pitch: 2f),
            Box(0f, -0.06f, -0.22f, 0.56f, 0.56f, 0.06f, Cold, pitch: 5f),
            Box(0f, -0.62f, -0.25f, 0.70f, 0.56f, 0.06f, Ash, pitch: 7f),
            Box(-0.26f, -0.96f, -0.28f, 0.26f, 0.44f, 0.06f, Cold, pitch: 10f),
            Box(0f, -1.02f, -0.28f, 0.24f, 0.44f, 0.06f, Ash, pitch: 12f),
            Box(0.26f, -0.92f, -0.28f, 0.26f, 0.40f, 0.06f, Cold, pitch: 9f),
            Box(0f, 1.00f, -0.19f, 0.34f, 0.30f, 0.06f, Ash, pitch: -10f),
            Box(0f, 1.175f, -0.19f, 0.32f, 0.03f, 0.06f, Bone));

        // A broad shoulder mantle, layered and drooping at the points, over a short tapered back panel.
        Add("worn.back.grave-mantle", ArmourMount.Back,
            Box(0f, 0.84f, -0.08f, 0.50f, 0.10f, 0.40f, Cold),
            Box(0.28f, 0.80f, -0.08f, 0.24f, 0.10f, 0.40f, Cold, roll: -18f),
            Box(-0.28f, 0.80f, -0.08f, 0.24f, 0.10f, 0.40f, Cold, roll: 18f),
            Box(0f, 0.66f, -0.10f, 0.66f, 0.10f, 0.36f, Ash),
            Box(0f, 0.905f, -0.08f, 0.40f, 0.03f, 0.30f, Bone),
            Box(0f, 0.30f, -0.19f, 0.40f, 0.56f, 0.06f, Cold, pitch: 3f),
            Box(-0.17f, -0.29f, -0.22f, 0.30f, 0.62f, 0.06f, Ash, pitch: 6f),
            Box(0.18f, -0.24f, -0.25f, 0.32f, 0.52f, 0.06f, Cold, pitch: 8f));

        // Not cloth: two thin glowing planes splayed from the shoulder blades, bone spars and a dark root.
        Add("worn.back.gossamer-wings", ArmourMount.Back,
            Box(0.36f, 0.72f, -0.28f, 0.32f, 0.50f, 0.02f, ArmourMaterial.Arcane, roll: -12f),
            Box(-0.36f, 0.72f, -0.28f, 0.32f, 0.50f, 0.02f, ArmourMaterial.Arcane, roll: 12f),
            Box(0.22f, 0.70f, -0.26f, 0.04f, 0.50f, 0.04f, Bone),
            Box(-0.22f, 0.70f, -0.26f, 0.04f, 0.50f, 0.04f, Bone),
            Box(0f, 0.62f, -0.24f, 0.20f, 0.20f, 0.06f, Cold));

        // ---- Companion (torso frame): a free-floating wisp, no envelope.
        Add("worn.companion.ember-wisp", ArmourMount.Companion,
            Box(0.42f, 0.95f, -0.10f, 0.12f, 0.12f, 0.12f, ArmourMaterial.Ember),
            Box(0.42f, 0.95f, -0.10f, 0.12f, 0.12f, 0.12f, ArmourMaterial.Ember, pitch: 45f, roll: 45f),
            Box(0.42f, 0.80f, -0.14f, 0.05f, 0.12f, 0.05f, ArmourMaterial.Ember),
            Box(0.42f, 0.84f, -0.10f, 0.16f, 0.03f, 0.16f, Iron));

        return models;
    }
}
