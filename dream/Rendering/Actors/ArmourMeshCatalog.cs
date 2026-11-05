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
    float RollDegrees = 0f)
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

// The procedural meshes of the armour a player wears, by per-item visual id. The base figure is
// already dressed (a hood, a kettle helm, a cloak, a backplate, knee cops), so every piece OVERSIZES
// AND ENCLOSES the baked geometry at its node: the baked helm or cloak becomes an interior surface.
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

    public static Vector4 BaseColor(ArmourMaterial material) => material switch
    {
        ArmourMaterial.Steel => new Vector4(0.36f, 0.39f, 0.46f, 1f),
        ArmourMaterial.DarkIron => new Vector4(0.17f, 0.17f, 0.20f, 1f),
        ArmourMaterial.Leather => new Vector4(0.13f, 0.08f, 0.06f, 1f),
        ArmourMaterial.ClothCold => new Vector4(0.086f, 0.102f, 0.157f, 1f),
        ArmourMaterial.ClothAsh => new Vector4(0.118f, 0.125f, 0.141f, 1f),
        ArmourMaterial.ClothRed => new Vector4(0.353f, 0.118f, 0.102f, 1f),
        ArmourMaterial.Bone => new Vector4(0.62f, 0.58f, 0.48f, 1f),
        ArmourMaterial.Gilt => new Vector4(0.50f, 0.37f, 0.14f, 1f),
        ArmourMaterial.Arcane => new Vector4(0.35f, 0.18f, 0.55f, 1f),
        ArmourMaterial.Ember => new Vector4(0.95f, 0.45f, 0.12f, 1f),
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
        float roll = 0f)
    {
        return new ArmourPart(new Vector3(x, y, z), new Vector3(sx, sy, sz), material, pitch, roll);
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

        // ---- Head: must contain y 0..0.56, |x| <= 0.23, |z| <= 0.20 (brim hw 0.204, hood apex 0.537).

        // A soft cap: a wide dark brim, a hanging nape flap and a bone button.
        Add("worn.head.leather-cap", ArmourMount.Head,
            Box(0f, 0.28f, 0f, 0.48f, 0.58f, 0.42f, Leather),
            Box(0f, 0.12f, -0.20f, 0.52f, 0.30f, 0.12f, Cold),
            Box(0f, 0.50f, 0f, 0.58f, 0.06f, 0.50f, Ash),
            Box(0f, 0.61f, 0f, 0.14f, 0.06f, 0.14f, Bone));

        // An open kettle: broad steel brim, cheek guards, a nasal bar and a mail aventail.
        Add("worn.head.iron-helm", ArmourMount.Head,
            Box(0f, 0.285f, 0f, 0.52f, 0.58f, 0.46f, Iron),
            Box(0f, 0.57f, 0f, 0.40f, 0.10f, 0.36f, Steel),
            Box(0f, 0.40f, 0f, 0.68f, 0.04f, 0.60f, Steel),
            Box(0f, 0.22f, 0.25f, 0.06f, 0.30f, 0.06f, Steel),
            Box(0.27f, 0.15f, 0.02f, 0.06f, 0.26f, 0.30f, Iron),
            Box(-0.27f, 0.15f, 0.02f, 0.06f, 0.26f, 0.30f, Iron),
            Box(0f, 0.10f, -0.25f, 0.48f, 0.26f, 0.08f, Cold));

        // A closed bucket helm: tall, boxy, a visor slit, a breath bar and a red plume.
        Add("worn.head.greathelm", ArmourMount.Head,
            Box(0f, 0.30f, 0f, 0.58f, 0.66f, 0.52f, Steel),
            Box(0f, 0.34f, 0.27f, 0.46f, 0.07f, 0.04f, Iron),
            Box(0f, 0.14f, 0.27f, 0.20f, 0.18f, 0.04f, Iron),
            Box(0f, 0.65f, 0f, 0.50f, 0.04f, 0.46f, Steel),
            Box(0f, 0.74f, -0.02f, 0.08f, 0.14f, 0.40f, Red),
            Box(0f, 0.02f, 0f, 0.62f, 0.08f, 0.56f, Iron));

        // ---- Body: must contain y 0.18..0.80, |x| <= 0.26, z -0.20..+0.17; shoulders to 0.60.

        // A boiled-leather vest: laced front, a belt and a bone collar.
        Add("worn.body.leather-vest", ArmourMount.Torso,
            Box(0f, 0.50f, -0.015f, 0.54f, 0.66f, 0.40f, Leather),
            Box(0f, 0.48f, 0.19f, 0.18f, 0.50f, 0.03f, Cold),
            Box(0f, 0.22f, 0f, 0.58f, 0.10f, 0.44f, Ash),
            Box(0f, 0.84f, 0f, 0.30f, 0.04f, 0.26f, Bone));

        // A mail shirt: dark mesh, a steel mantle over the shoulders, a long skirt and sleeve stubs.
        Add("worn.body.chain-hauberk", ArmourMount.Torso,
            Box(0f, 0.50f, -0.015f, 0.56f, 0.66f, 0.40f, Iron),
            Box(0f, 0.80f, 0f, 0.74f, 0.07f, 0.46f, Steel),
            Box(0f, 0.17f, 0f, 0.62f, 0.14f, 0.46f, Steel),
            Box(0f, 0.30f, 0f, 0.60f, 0.06f, 0.44f, Cold),
            Box(0.40f, 0.70f, 0f, 0.18f, 0.22f, 0.30f, Iron),
            Box(-0.40f, 0.70f, 0f, 0.18f, 0.22f, 0.30f, Iron));

        // A plate cuirass: a ridge, big pauldrons, a dark fauld and a red sash.
        Add("worn.body.plate-cuirass", ArmourMount.Torso,
            Box(0f, 0.52f, 0f, 0.62f, 0.64f, 0.42f, Steel),
            Box(0f, 0.52f, 0.215f, 0.08f, 0.56f, 0.04f, Iron),
            Box(0.46f, 0.80f, 0f, 0.28f, 0.18f, 0.36f, Steel),
            Box(-0.46f, 0.80f, 0f, 0.28f, 0.18f, 0.36f, Steel),
            Box(0f, 0.22f, 0f, 0.58f, 0.12f, 0.46f, Iron),
            Box(0f, 0.35f, 0.215f, 0.50f, 0.06f, 0.03f, Red));

        // ---- Hands (one arm, centred on the arm's own axis): y -0.95..-0.55, |x| <= 0.11, |z| <= 0.11.

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

        // ---- Legs (one leg): y -0.75..-0.10, |x| <= 0.14, |z| <= 0.14 (cuisse, knee cop at -0.61).

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

        // ---- Feet (one leg): y -1.30..-0.88, |x| <= 0.13, z -0.05..+0.22.

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

        // ---- Back (torso frame): the cloaks sit strictly outside the baked cloak (half-width 0.326,
        // back -0.36, hem -0.475, top 0.836): half-width >= 0.36, back <= -0.38, hem <= -0.50, top >= 0.84.

        // Every cloak is a TAPERED STACK, never one plank: narrow at the shoulders (<= 0.54), widest near the
        // hem, each box further back and pitched a little more than the one above (positive pitch swings
        // the bottom edge backwards), so the lengths take different light and the hem hangs away from
        // the body. Hem boxes are inset or split into tabs so the bottom outline is not a square line.

        // Short and ragged: a leather yoke, a stepped body and three tabs of different lengths.
        Add("worn.back.travel-cloak", ArmourMount.Back,
            Box(0f, 0.84f, -0.24f, 0.52f, 0.10f, 0.30f, Leather),
            Box(0f, 0.50f, -0.36f, 0.54f, 0.50f, 0.12f, Ash, pitch: 2f),
            Box(0f, 0.02f, -0.40f, 0.64f, 0.50f, 0.10f, Ash, pitch: 5f),
            Box(0f, -0.30f, -0.44f, 0.26f, 0.36f, 0.08f, Ash, pitch: 9f),
            Box(0.29f, -0.33f, -0.43f, 0.20f, 0.44f, 0.08f, Ash, pitch: 8f),
            Box(-0.29f, -0.27f, -0.43f, 0.20f, 0.32f, 0.08f, Ash, pitch: 8f),
            Box(0f, 0.90f, -0.22f, 0.10f, 0.04f, 0.10f, Steel));

        // Long, to the calf: a stepped trapezoid, a hem of three folds of unequal length and a raised standing collar (not a hood).
        Add("worn.back.hooded-cloak", ArmourMount.Back,
            Box(0f, 0.60f, -0.32f, 0.54f, 0.72f, 0.18f, Ash, pitch: 2f),
            Box(0f, -0.06f, -0.38f, 0.70f, 0.56f, 0.14f, Cold, pitch: 5f),
            Box(0f, -0.62f, -0.44f, 0.80f, 0.56f, 0.12f, Ash, pitch: 7f),
            Box(-0.27f, -0.96f, -0.48f, 0.30f, 0.44f, 0.12f, Cold, pitch: 10f),
            Box(0f, -1.02f, -0.50f, 0.28f, 0.44f, 0.12f, Ash, pitch: 12f),
            Box(0.27f, -0.92f, -0.47f, 0.30f, 0.40f, 0.12f, Cold, pitch: 9f),
            Box(0f, 1.00f, -0.28f, 0.46f, 0.30f, 0.14f, Ash, pitch: -10f),
            Box(0f, 1.175f, -0.30f, 0.44f, 0.03f, 0.12f, Bone));

        // A broad shoulder mantle, layered and drooping at the points, over a short tapered back panel.
        Add("worn.back.grave-mantle", ArmourMount.Back,
            Box(0f, 0.84f, -0.10f, 0.64f, 0.10f, 0.50f, Cold),
            Box(0.40f, 0.80f, -0.10f, 0.36f, 0.10f, 0.50f, Cold, roll: -18f),
            Box(-0.40f, 0.80f, -0.10f, 0.36f, 0.10f, 0.50f, Cold, roll: 18f),
            Box(0f, 0.66f, -0.12f, 0.84f, 0.10f, 0.46f, Ash),
            Box(0f, 0.905f, -0.10f, 0.50f, 0.03f, 0.38f, Bone),
            Box(0f, 0.30f, -0.34f, 0.52f, 0.56f, 0.10f, Cold, pitch: 3f),
            Box(-0.19f, -0.29f, -0.39f, 0.38f, 0.62f, 0.08f, Ash, pitch: 6f),
            Box(0.21f, -0.24f, -0.42f, 0.42f, 0.52f, 0.08f, Cold, pitch: 8f));

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
