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
    Ember,

    // Enamelled indigo plate: a deep violet-blue lacquer over steel, the field colour of the royal
    // sorcerer regalia. It exists because the palette had no indigo that was not nearly black -
    // ClothCold is (0.07, 0.078, 0.10) and reads black at this zoom - and the set the design calls
    // for is indigo plate with gold trim, not black plate with gold trim. Metallic like steel, so
    // it answers a torch, but dark enough to stay far under the albedo ceiling.
    Indigo
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
// about 1.6x the body at its node reads as a block, not as clothing - EXCEPT in the two bands named in
// the Head and Body sections below, where the shoulders and the helm crest are allowed to be the
// silhouette, because in this art direction they ARE the silhouette.
// At the shipped isometric zoom the whole figure is 28x101 pixels, so a piece differs by outline,
// mass and value only; each has 3..14 parts, a light top facet and a deep dark mass, because the parts
// get no bake and no ambient occlusion. An id this catalog does not know draws nothing at all: a
// wrong box on the head is worse than none.
//
// THE SIX THINGS A WORN PIECE IS JUDGED ON (pass 3, from the reference silhouette), in order:
//   1. the shoulders are the biggest element - pauldrons rise above the shoulder line, stand well past
//      the body and end in one to three upswept Horns;
//   2. the torso is an hourglass - wide chest, cinched waist, flared fauld over the hips;
//   3. the head carries a tall vertical crest, as tall again as the helm on the boldest set;
//   4. a centre panel hangs between the legs toward the knee;
//   5. ends FLARE - gauntlet cuffs, boot tops, hems - because a tube reads as a pipe;
//   6. layered lames with visible steps, and a thin bright trim line (Steel or Gilt) along the borders
//      of a dark (DarkIron, Leather) plate.
// The direction stays DARK: the shape comes from the references, the colour does not. DarkIron is the
// structural material, Steel and Gilt are edges and rims, Bone is an ornament, and no light material
// goes on a large up-facing surface, because the camera is pitched 50 degrees down and an up-facing
// normal takes the sky ambient before any light reaches it.
//
// PASS 5 (profiled shells): a shoulder cap is a Pauldron, a torso a Cuirass, a limb or boot shaft a Greave, a layered
// fauld / cuisse a Lame, and the gossamer wings a Membrane; Plate stays only on the side plates that wrap the body.
// All five open toward -Z like Plate, so a Pauldron is turned with pitch: -90f exactly as the Plate cap was.
// Every part names an EquipmentShape, and none of them is a plain Box: a helm skull or a knee cop is a
// Dome, a pauldron / cuirass / greave / bracer / sleeve is a Plate (a 150 degree shell that WRAPS the
// limb instead of boxing it), a collar or mail row is a Ring, a cloak or tabard or aventail is a Panel,
// a stud or barb is a Spike, a sabaton toe or nasal bar is a Taper, and anything honestly rectilinear
// (a lame, a belt, a buckle, a visor slit) is a BevelBox, whose chamfers catch a light value its faces
// do not. Pass 3 adds the two shapes the reference silhouette is built out of: a pauldron barb, a helm
// crest point or a thigh claw is a Horn (a cone swept along an arc, so the weight of the outline lands
// somewhere other than straight up), and a cuff, a boot top, a fauld skirt or a helm brim is a Flare
// (a hollow bell). Sizes are authored in the part's OWN frame, before its rotation.
//
// Rotation conventions used below. A Plate opens toward its own -Z, so a sleeve or cheek guard is
// yawed +-90 degrees to put the opening against the body; yaw also swaps which authored axis becomes
// world X and which becomes world Z. A Panel is thin in its own Z, so yaw: 90f hangs it at the side of
// a limb and pitch: 90f lays it flat (a boot sole). A Dome caps its own +Y, so pitch: 90f makes a knee
// cop or a toe cap that bulges forward. A Spike points along its own +Y. A Horn is thick at its own -Y
// and curves toward its own +Z as it rises; a Flare is narrow at its own -Y and bells out at +Y, so
// pitch: 180f turns it into a skirt, a brim or a hem that widens as it falls.
//
// TWO THINGS PASS 4 GOT WRONG, both of them orientation, both found in the engine and not in a test:
//
//   A PLATE THAT CAPS A LIMB MUST BE TURNED. Unrotated, a Plate's opening faces -Z, which is right for
//   a cuirass or a greave (the body fills the gap) and wrong for a shoulder cap: the camera looks down
//   and forward, sees straight into the hollow, and the pauldron reads as a detached horseshoe floating
//   beside the figure. The big pauldrons are now `pitch: -90f`, which lays the shell axis front-to-back
//   and turns the opening DOWNWARD onto the shoulder. Note what that does to the authored Size: the
//   part's own Y becomes world DEPTH and its own Z becomes world HEIGHT, so a cap authored
//   (0.46, 0.30, 0.26) is 0.46 wide, 0.30 deep and 0.26 tall. EquipmentShape.cs states the rule; the
//   catalog test "no plate off the midline is left unrotated" is what stops it coming back.
//
//   A HORN'S ROLL IS APPLIED BEFORE ITS YAW. Rotations go pitch, then roll, then yaw, so on a horn that
//   is swept backwards with yaw: 180f the yaw also flips the roll's direction: a roll of -30 on the +X
//   side leans the horn INWARD over the figure, not outward. Every backswept horn therefore carries a
//   POSITIVE roll on the +X side and a negative roll on -X - the opposite of what reads naturally - and
//   the mirrored pair is authored that way on purpose. A Horn also needs real depth in its own Z to show
//   its sweep: the mesh is fitted to its authored box, so a horn authored 0.07 deep and 0.40 tall is a
//   straight spike with a kink, and one authored a third as deep as it is tall is a horn.
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
        // Max channel 0.185: at the 2.17 peak gain this draws about (0.21, 0.17, 0.40), a clear
        // deep indigo that never clips. The reference art it comes from is far brighter; that art
        // was authored for a tonemapped renderer and this one has none (see the header).
        ArmourMaterial.Indigo => new Vector4(0.095f, 0.080f, 0.185f, 1f),
        _ => new Vector4(0.3f, 0.3f, 0.3f, 1f)
    };

    // Added after lighting: only the glowing materials are not zero.
    //
    // Arcane was (0.30, 0.12, 0.55) and that number is why the gossamer wings read as a flat violet
    // chevron however the mesh was sculpted. Emissive is added AFTER the gain, so it is a constant
    // the shading cannot modulate: at 0.55 of blue on an albedo whose lit blue reaches about 0.37,
    // three fifths of every wing texel was a value no light could change, and the whole piece sat at
    // its ceiling. It is now about a third of that, which leaves the lit range in charge of the form
    // and still glows clearly at midnight, where an emissive is actually for.
    public static Vector3 Emissive(ArmourMaterial material) => material switch
    {
        ArmourMaterial.Arcane => new Vector3(0.09f, 0.035f, 0.17f),
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
        const ArmourMaterial Indigo = ArmourMaterial.Indigo;

        const EquipmentShape BevelBox = EquipmentShape.BevelBox;
        const EquipmentShape Shaft = EquipmentShape.Shaft;
        const EquipmentShape Taper = EquipmentShape.Taper;
        const EquipmentShape Plate = EquipmentShape.Plate;
        const EquipmentShape Dome = EquipmentShape.Dome;
        const EquipmentShape Spike = EquipmentShape.Spike;
        const EquipmentShape Ring = EquipmentShape.Ring;
        const EquipmentShape Gem = EquipmentShape.Gem;
        const EquipmentShape Panel = EquipmentShape.Panel;
        const EquipmentShape Horn = EquipmentShape.Horn;
        const EquipmentShape Flare = EquipmentShape.Flare;
        const EquipmentShape Pauldron = EquipmentShape.Pauldron;
        const EquipmentShape Cuirass = EquipmentShape.Cuirass;
        const EquipmentShape Greave = EquipmentShape.Greave;
        const EquipmentShape Lame = EquipmentShape.Lame;
        const EquipmentShape Membrane = EquipmentShape.Membrane;

        // ---- Head: sits on a bare head, y 0..0.42, |x| <= 0.12, |z| <= 0.13. A helm's shell is the head plus
        // a wall (0.04) plus a hand of clearance and its brim: main mass 0.32..0.355 wide (half-width 0.16..0.1775,
        // a shell 0.04..0.05 thick, 1.5-2 px at the actor's 0.0265 units per px), brim and rims at most 0.18.
        //
        // THE CEILING IS NOW A BAND (pass 3). Below y 0.42 - the top of the bare skull - nothing is wider than
        // 0.18, exactly as before: that is the head's half-width plus the 0.06 head-shell allowance (1.5x), and
        // a helm wider than that swallows the face. ABOVE y 0.42 a CREST is allowed, out to half-width 0.26 and
        // up to y 1.05, because the silhouette this set is aimed at carries its character above the skull - a
        // fin, a pair of swept horns, a crown of points, as tall again as the helm itself on the boldest helm.
        // Depth is unchanged at 0.195 either side of the band: a crest sweeps UP and BACK, not out to the sides.

        // A soft cap: a domed leather crown under a flared leather brim, a cloth nape flap, ear flaps, an ash
        // trim line round the crown, a small bone finial and a pair of short horns swept up and back. The
        // leather reading of the direction: low, soft and lumpy, with horns that are an accent, not a statement.
        Add("worn.head.leather-cap", ArmourMount.Head,
            Box(0f, 0.22f, 0f, 0.32f, 0.44f, 0.30f, Leather, shape: Dome),
            Box(0f, 0.12f, -0.16f, 0.32f, 0.26f, 0.06f, Cold, shape: Panel),
            Box(0f, 0.085f, 0f, 0.34f, 0.14f, 0.34f, Leather, pitch: 180f, shape: Flare),
            Box(0.155f, 0.10f, 0f, 0.22f, 0.20f, 0.04f, Leather, yaw: 90f, shape: Panel),
            Box(-0.155f, 0.10f, 0f, 0.22f, 0.20f, 0.04f, Leather, yaw: -90f, shape: Panel),
            Box(0f, 0.395f, 0f, 0.30f, 0.035f, 0.30f, Ash, shape: Ring),
            Box(0f, 0.47f, 0f, 0.09f, 0.06f, 0.09f, Bone, shape: Dome),
            Box(0.085f, 0.57f, -0.05f, 0.08f, 0.24f, 0.16f, Leather, roll: 16f, yaw: 180f, shape: Horn),
            Box(-0.085f, 0.57f, -0.05f, 0.08f, 0.24f, 0.16f, Leather, roll: -16f, yaw: 180f, shape: Horn),
            Box(0f, 0.30f, 0.15f, 0.09f, 0.07f, 0.06f, Gilt, shape: BevelBox),
            Box(0f, 0.17f, 0.155f, 0.26f, 0.05f, 0.05f, Gilt, shape: BevelBox));

        // An open kettle: a domed skull under a brim that FLARES down and out, a bright steel trim line along
        // the brim's border, wrapping cheek plates, a tapering nasal bar, and a three-point crest - a tall
        // centre horn with a shorter horn to each side - rooted in a gilt crown rim.
        Add("worn.head.iron-helm", ArmourMount.Head,
            Box(0f, 0.22f, 0f, 0.32f, 0.44f, 0.32f, Iron, shape: Dome),
            Box(0f, 0.10f, -0.16f, 0.32f, 0.24f, 0.06f, Cold, shape: Panel),
            Box(0f, 0.085f, 0f, 0.345f, 0.13f, 0.345f, Iron, pitch: 180f, shape: Flare),
            Box(0f, 0.155f, 0f, 0.33f, 0.03f, 0.33f, Steel, shape: Ring),
            Box(0.148f, 0.15f, 0.02f, 0.26f, 0.26f, 0.06f, Iron, yaw: 90f, shape: Greave),
            Box(-0.148f, 0.15f, 0.02f, 0.26f, 0.26f, 0.06f, Iron, yaw: -90f, shape: Greave),
            Box(0f, 0.22f, 0.17f, 0.05f, 0.26f, 0.04f, Steel, pitch: 180f, shape: Taper),
            Box(0f, 0.40f, 0f, 0.06f, 0.10f, 0.30f, Iron, shape: BevelBox),
            Box(0f, 0.455f, 0f, 0.22f, 0.035f, 0.26f, Gilt, shape: Ring),
            Box(0f, 0.64f, -0.06f, 0.07f, 0.38f, 0.20f, Iron, yaw: 180f, shape: Horn),
            Box(0.07f, 0.59f, -0.05f, 0.08f, 0.28f, 0.16f, Iron, roll: 18f, yaw: 180f, shape: Horn),
            Box(-0.07f, 0.59f, -0.05f, 0.08f, 0.28f, 0.16f, Iron, roll: -18f, yaw: 180f, shape: Horn),
            Box(0.155f, 0.30f, 0.05f, 0.03f, 0.03f, 0.03f, Steel, roll: -90f, shape: Spike),
            Box(-0.155f, 0.30f, 0.05f, 0.03f, 0.03f, 0.03f, Steel, roll: 90f, shape: Spike));

        // A closed great helm, and the boldest head in the set: a tall BLACKENED shell (the skull is the largest
        // up-facing surface on the figure, so it carries the dark material and the steel is only its trim line)
        // under a flared brim, a visor slit, a breath plate, gilt brow barbs and a steel brow bar, and above it
        // all a CREST as tall again as the helm - a centre horn reaching y 1.04 with a swept horn to each side
        // and a red plume between them.
        // The TWO STEEL BORDERS are the piece's whole light budget and are load-bearing, so they are stated
        // rather than left to be tuned away: a narrow crown rim at the crest's root (the border between the
        // dark dome and the crest) and a steel lip rolled under the brim (a Flare, pitch 180, so the camera
        // sees its glancing wall and not a lid). Blackening either of them costs the dressed figure about 0.055
        // of M3 spread, measured; see the check_char note on WORN_SET_SPREAD_MIN.
        Add("worn.head.greathelm", ArmourMount.Head,
            Box(0f, 0.23f, 0f, 0.34f, 0.48f, 0.34f, Iron, shape: Dome),
            Box(0f, 0.355f, 0.175f, 0.30f, 0.07f, 0.03f, Steel, shape: BevelBox),
            Box(0f, 0.14f, 0.18f, 0.14f, 0.16f, 0.03f, Iron, shape: BevelBox),
            Box(0f, 0.085f, 0f, 0.355f, 0.13f, 0.355f, Iron, pitch: 180f, shape: Flare),
            Box(0f, 0.035f, 0f, 0.36f, 0.05f, 0.36f, Steel, pitch: 180f, shape: Flare),
            Box(0f, 0.48f, 0f, 0.34f, 0.05f, 0.34f, Steel, shape: Ring),
            Box(0f, 0.72f, -0.07f, 0.07f, 0.54f, 0.24f, Iron, yaw: 180f, shape: Horn),
            Box(0.105f, 0.62f, -0.06f, 0.09f, 0.38f, 0.18f, Iron, roll: 20f, yaw: 180f, shape: Horn),
            Box(-0.105f, 0.62f, -0.06f, 0.09f, 0.38f, 0.18f, Iron, roll: -20f, yaw: 180f, shape: Horn),
            Box(0f, 0.58f, -0.02f, 0.30f, 0.14f, 0.06f, Red, yaw: 90f, shape: Panel),
            Box(0f, 0.02f, 0f, 0.34f, 0.06f, 0.34f, Iron, shape: Ring),
            Box(0.13f, 0.40f, 0.10f, 0.045f, 0.10f, 0.08f, Gilt, roll: -35f, shape: Horn),
            Box(-0.13f, 0.40f, 0.10f, 0.045f, 0.10f, 0.08f, Gilt, roll: 35f, shape: Horn),
            Box(0f, 0.30f, 0.165f, 0.22f, 0.04f, 0.04f, Steel, shape: BevelBox));

        // The royal sorcerer's crown, and the first piece in the catalog whose FIELD is enamelled Indigo rather
        // than blackened iron: a close indigo hood shell over a dark mask, a gold brow band with a pointed gold
        // nasal guard dropped from it, and FIVE gold crown points rooted in that band - a tall centre horn with
        // two shorter pairs fanning out to the sides, every one of them a Horn so the crown reads as swept
        // regalia and not as a ring of studs. The crest is the only thing allowed past half-width 0.18, and it
        // uses the band the head section opens above y 0.42; the outer pair is the widest at 0.25.
        // The one light material is a narrow Steel brow bar over the nasal guard: gold is the colour of this
        // set, but Gilt's luma is 0.24 and it is not a LIGHT material, so without that bar the crown would own
        // no bright facet at all.
        Add("worn.head.sorcerer-crown", ArmourMount.Head,
            Box(0f, 0.22f, 0f, 0.34f, 0.46f, 0.34f, Indigo, shape: Dome),
            Box(0f, 0.13f, 0.155f, 0.22f, 0.26f, 0.06f, Leather, shape: BevelBox),
            Box(0f, 0.075f, 0f, 0.35f, 0.13f, 0.35f, Indigo, pitch: 180f, shape: Flare),
            Box(0.15f, 0.14f, 0.02f, 0.24f, 0.24f, 0.05f, Indigo, yaw: 90f, shape: Greave),
            Box(-0.15f, 0.14f, 0.02f, 0.24f, 0.24f, 0.05f, Indigo, yaw: -90f, shape: Greave),
            Box(0f, 0.33f, 0f, 0.345f, 0.04f, 0.345f, Gilt, shape: Ring),
            Box(0f, 0.19f, 0.165f, 0.05f, 0.24f, 0.045f, Gilt, pitch: 180f, shape: Taper),
            Box(0f, 0.295f, 0.16f, 0.20f, 0.035f, 0.035f, Steel, shape: BevelBox),
            Box(0f, 0.44f, 0f, 0.26f, 0.035f, 0.28f, Gilt, shape: Ring),
            Box(0f, 0.70f, -0.05f, 0.08f, 0.50f, 0.22f, Gilt, yaw: 180f, shape: Horn),
            Box(0.085f, 0.62f, -0.045f, 0.08f, 0.38f, 0.18f, Gilt, roll: 18f, yaw: 180f, shape: Horn),
            Box(-0.085f, 0.62f, -0.045f, 0.08f, 0.38f, 0.18f, Gilt, roll: -18f, yaw: 180f, shape: Horn),
            Box(0.15f, 0.52f, -0.04f, 0.07f, 0.28f, 0.15f, Gilt, roll: 30f, yaw: 180f, shape: Horn),
            Box(-0.15f, 0.52f, -0.04f, 0.07f, 0.28f, 0.15f, Gilt, roll: -30f, yaw: 180f, shape: Horn));

        // ---- Body: sits on the tunic. Chest y 0..0.86 to |x| <= 0.19, hip skirt to |x| <= 0.26 (y 0..0.30),
        // z -0.14..+0.14.
        //
        // THE CEILING IS NOW A BAND (pass 3), and the band is the whole point of the pass. The shoulders ARE
        // the silhouette: a pauldron that reads at 28 px rises clearly above the shoulder line, stands well
        // past the body and ends in an upswept barb, and none of that fits under the old flat 0.416 ceiling.
        // So: a part whose CENTRE is at or above y 0.70 - the shoulder band - may reach half-width 0.62 (2.4x
        // the 0.26 base). Everything below it keeps the old 0.416, UNCHANGED, which is what keeps the waist a
        // waist: the outline is meant to be an hourglass, wide at the shoulders, cinched above the belt and
        // flared again over the hips, and opening the ceiling at the waist too would just make it a block.
        // Depth stays 0.24 everywhere; a pauldron grows sideways and upward, not forward.
        //
        // The shared skeleton of all three bodies, so the hourglass is the same read at every tier:
        //   upper chest plate   y 0.44..0.90   half 0.22..0.24
        //   waist plate         y 0.28..0.54   half 0.20        <- the cinch
        //   fauld / skirt       y 0.00..0.33   half 0.27..0.28  <- a Flare, so it widens as it falls
        //   pauldrons           y 0.69..0.97   half 0.46..0.58  <- plus horns swept up and back
        //   hanging lappet      y -0.31..0.25                   <- the centre panel every reference has
        // The lappet is why this mount's limit reaches below the torso: it hangs in FRONT of the thighs
        // (z ~ +0.16) toward the knee, which is the fourth point of the silhouette brief.
        //
        // PASS 4: THE SHOULDERS HAVE TO MEET THE BODY. The pass-3 pauldrons stood at half-width 0.60 with
        // their inner edge at 0.20, just outside the chest's 0.195, and the gap read as a prop hanging in
        // the air beside the figure. Every shoulder cap now BURIES its inner edge in the torso mass (inner
        // edge <= 0.13, well inside the chest), and the gorget ring was widened to 0.34 - the widest a light
        // level ring may be - so there is a visible yoke running from one cap to the other across the throat.
        // Overlap costs nothing here: nothing is sorted and nothing z-fights at this scale, so a big outer
        // piece is biased inward until it touches rather than placed to just miss.
        //
        // And the hierarchy: the shoulders are the statement, the helm crest is second, everything else
        // supports them. The lappets were 0.64..0.76 long and competed with both; they are 0.52..0.56 now.

        // A boiled-leather vest: a chest shell over a cinched waist and a belt, an ash skirt flaring over the
        // hips with a gilt trim line at its hem, a cloth lappet hanging to the thigh, a bone gorget, and a
        // rounded leather pauldron over each shoulder ending in a short horn. Leather's reading of the
        // direction is soft: the pauldrons are domed and lumpy rather than plated, and the horns are small.
        Add("worn.body.leather-vest", ArmourMount.Torso,
            Box(0f, 0.69f, 0f, 0.48f, 0.40f, 0.34f, Leather, shape: Cuirass),
            Box(0f, 0.41f, 0f, 0.40f, 0.24f, 0.32f, Leather, shape: Lame),
            Box(0f, 0.16f, 0f, 0.56f, 0.33f, 0.34f, Ash, pitch: 180f, shape: Flare),
            Box(0f, 0.60f, 0.165f, 0.16f, 0.36f, 0.03f, Cold, shape: Panel),
            Box(0f, 0.875f, 0f, 0.34f, 0.05f, 0.26f, Bone, shape: Ring),
            Box(0f, 0.33f, 0f, 0.42f, 0.06f, 0.34f, Leather, shape: BevelBox),
            Box(0f, 0.33f, 0.18f, 0.08f, 0.08f, 0.04f, Gilt, shape: BevelBox),
            Box(0f, 0.63f, 0f, 0.49f, 0.05f, 0.345f, Leather, shape: BevelBox),
            Box(0.33f, 0.82f, 0f, 0.42f, 0.32f, 0.22f, Leather, pitch: -90f, shape: Pauldron),
            Box(-0.33f, 0.82f, 0f, 0.42f, 0.32f, 0.22f, Leather, pitch: -90f, shape: Pauldron),
            Box(0.42f, 0.88f, -0.06f, 0.09f, 0.24f, 0.18f, Leather, roll: 30f, yaw: 180f, shape: Horn),
            Box(-0.42f, 0.88f, -0.06f, 0.09f, 0.24f, 0.18f, Leather, roll: -30f, yaw: 180f, shape: Horn),
            Box(0f, -0.02f, 0.165f, 0.15f, 0.54f, 0.035f, Ash, shape: Panel),
            Box(0f, 0.015f, 0f, 0.55f, 0.035f, 0.34f, Gilt, shape: Ring));

        // A mail shirt: the same hourglass, but read as a dense EVEN mass of blackened rings - every tier the
        // same value, the steps made by mail rows rather than by trim - with a modest flare at the hips, a
        // domed shoulder mass carrying one short barb each side, a cold-cloth lappet, and a small steel gorget
        // as the only bright rim on the piece.
        Add("worn.body.chain-hauberk", ArmourMount.Torso,
            Box(0f, 0.69f, 0f, 0.46f, 0.40f, 0.34f, Iron, shape: Cuirass),
            Box(0f, 0.41f, 0f, 0.40f, 0.24f, 0.32f, Iron, shape: Lame),
            Box(0f, 0.16f, 0f, 0.54f, 0.33f, 0.34f, Iron, pitch: 180f, shape: Flare),
            Box(0f, 0.86f, 0f, 0.24f, 0.045f, 0.24f, Steel, shape: Ring),
            Box(0.28f, 0.79f, 0f, 0.36f, 0.32f, 0.20f, Iron, pitch: -90f, shape: Pauldron),
            Box(-0.28f, 0.79f, 0f, 0.36f, 0.32f, 0.20f, Iron, pitch: -90f, shape: Pauldron),
            Box(0.26f, 0.66f, 0f, 0.26f, 0.24f, 0.18f, Iron, yaw: 90f, shape: Plate),
            Box(-0.26f, 0.66f, 0f, 0.26f, 0.24f, 0.18f, Iron, yaw: -90f, shape: Plate),
            Box(0.40f, 0.86f, -0.05f, 0.08f, 0.22f, 0.16f, Iron, roll: 34f, yaw: 180f, shape: Horn),
            Box(-0.40f, 0.86f, -0.05f, 0.08f, 0.22f, 0.16f, Iron, roll: -34f, yaw: 180f, shape: Horn),
            Box(0f, 0.33f, 0f, 0.43f, 0.05f, 0.345f, Iron, shape: Ring),
            Box(0f, 0.58f, 0f, 0.47f, 0.04f, 0.35f, Iron, shape: Ring),
            Box(0f, -0.01f, 0.155f, 0.14f, 0.52f, 0.04f, Cold, shape: Panel),
            Box(0f, 0.01f, 0f, 0.53f, 0.035f, 0.33f, Iron, shape: Ring));

        // A plate cuirass, the full statement of the brief. A wrapped breastplate (a vertical arc, so the camera
        // sees its glancing front and only the edge of its top rim - that is why the cuirass may stay steel while
        // the shoulders may not) with a raised steel keel ridge; a cinched iron waist under a red sash; a fauld
        // flaring over the hips with a gilt trim line at its hem; an iron tasset hanging to the knee; and the
        // shoulders: a big blackened pauldron SHELL each side, rising above the shoulder line and standing out
        // to half-width 0.60, each ending in an upswept horn that sweeps back past y 1.04, and each EDGED by a
        // narrow steel lame standing on its outer border.
        // That steel edge is the single most load-bearing light in the set and is why point 6 of the brief is
        // a rule and not a flourish: the pauldrons are the biggest mass on the figure, and when they were
        // blackened all the way to their borders the dressed figure's M3 spread fell from 0.3522 to 0.2481 -
        // through the floor - because a dark mass that size has nothing for the key light to catch. The plate
        // stays dark; only its border is bright. Do not blacken the edge to "unify" the piece.
        Add("worn.body.plate-cuirass", ArmourMount.Torso,
            Box(0f, 0.67f, 0f, 0.44f, 0.46f, 0.34f, Steel, shape: Cuirass),
            Box(0f, 0.68f, 0.17f, 0.09f, 0.46f, 0.04f, Steel, shape: BevelBox),
            Box(0f, 0.40f, 0f, 0.40f, 0.26f, 0.32f, Iron, shape: Lame),
            Box(0f, 0.16f, 0f, 0.56f, 0.33f, 0.34f, Iron, pitch: 180f, shape: Flare),
            Box(0f, 0.33f, 0.17f, 0.44f, 0.05f, 0.03f, Red, shape: Panel),
            Box(0f, 0.90f, 0f, 0.34f, 0.05f, 0.26f, Steel, shape: Ring),
            Box(0.35f, 0.84f, 0f, 0.46f, 0.30f, 0.26f, Iron, pitch: -90f, shape: Pauldron),
            Box(-0.35f, 0.84f, 0f, 0.46f, 0.30f, 0.26f, Iron, pitch: -90f, shape: Pauldron),
            Box(0.46f, 0.88f, -0.06f, 0.09f, 0.30f, 0.20f, Iron, roll: 30f, yaw: 180f, shape: Horn),
            Box(-0.46f, 0.88f, -0.06f, 0.09f, 0.30f, 0.20f, Iron, roll: -30f, yaw: 180f, shape: Horn),
            Box(0.55f, 0.84f, 0f, 0.06f, 0.26f, 0.34f, Steel, shape: BevelBox),
            Box(-0.55f, 0.84f, 0f, 0.06f, 0.26f, 0.34f, Steel, shape: BevelBox),
            Box(0f, -0.03f, 0.165f, 0.17f, 0.56f, 0.04f, Iron, shape: Panel),
            Box(0f, 0.02f, 0f, 0.55f, 0.04f, 0.34f, Gilt, shape: Ring));

        // The royal sorcerer's regalia, and the most ornate body in the catalog - but built in the same order as
        // the others, because ornament that competes with the silhouette just reads as noise at 28 px: the
        // shoulders first (an oversized indigo cap each side, buried in the chest at its inner edge, ending in a
        // GOLD upswept barb and edged by a gold filigree lame on its outer border), the high flared collar
        // second, and everything after that is support - a tapered indigo cuirass, a cinched waist under a gold
        // belt, a fauld flaring over the hips with a gold hem ring, and the diamond tabard hanging toward the
        // knee. The sternum gem is the only glowing thing on the figure's front and is deliberately small: it is
        // Arcane, so its emissive is added after the gain and a big one would be a lamp, not a jewel.
        // The gold is Gilt everywhere and never Steel: this set's bright edge is warm, and Gilt sits under the
        // light threshold, so the gem is the piece's only light facet by design.
        Add("worn.body.sorcerer-regalia", ArmourMount.Torso,
            Box(0f, 0.67f, 0f, 0.44f, 0.46f, 0.34f, Indigo, shape: Cuirass),
            Box(0f, 0.92f, 0f, 0.34f, 0.18f, 0.30f, Indigo, shape: Flare),
            Box(0f, 0.40f, 0f, 0.40f, 0.26f, 0.32f, Indigo, shape: Lame),
            Box(0f, 0.16f, 0f, 0.57f, 0.33f, 0.34f, Indigo, pitch: 180f, shape: Flare),
            Box(0f, 0.33f, 0f, 0.44f, 0.05f, 0.345f, Gilt, shape: Ring),
            Box(0f, 0.72f, 0.17f, 0.11f, 0.13f, 0.08f, ArmourMaterial.Arcane, shape: Gem),
            Box(0.35f, 0.84f, 0f, 0.48f, 0.32f, 0.28f, Indigo, pitch: -90f, shape: Pauldron),
            Box(-0.35f, 0.84f, 0f, 0.48f, 0.32f, 0.28f, Indigo, pitch: -90f, shape: Pauldron),
            Box(0.46f, 0.88f, -0.06f, 0.09f, 0.30f, 0.20f, Gilt, roll: 30f, yaw: 180f, shape: Horn),
            Box(-0.46f, 0.88f, -0.06f, 0.09f, 0.30f, 0.20f, Gilt, roll: -30f, yaw: 180f, shape: Horn),
            Box(0.555f, 0.84f, 0f, 0.055f, 0.26f, 0.34f, Gilt, shape: BevelBox),
            Box(-0.555f, 0.84f, 0f, 0.055f, 0.26f, 0.34f, Gilt, shape: BevelBox),
            Box(0f, -0.03f, 0.165f, 0.18f, 0.56f, 0.04f, Indigo, roll: 45f, shape: Panel),
            Box(0f, 0.015f, 0f, 0.56f, 0.035f, 0.345f, Gilt, shape: Ring));

        // ---- Hands (one arm, centred on the arm's own axis): cover y -0.95..-0.55, |x| <= 0.09, |z| <= 0.09.
        // A straight tube reads as a pipe, so every glove now TRUMPETS at the cuff: a Flare at y ~ -0.55 with a
        // narrow rim along its lip and a short horn each side, over a wrapped shell and a stepped lame or two.

        Add("worn.hands.leather-gloves", ArmourMount.Arms,
            Box(0f, -0.76f, 0f, 0.24f, 0.44f, 0.24f, Leather, shape: Greave),
            Box(0f, -0.56f, 0f, 0.32f, 0.16f, 0.32f, Ash, shape: Flare),
            Box(0f, -0.47f, 0f, 0.22f, 0.03f, 0.22f, Bone, shape: Ring),
            Box(0f, -0.94f, 0f, 0.22f, 0.08f, 0.26f, Cold, shape: BevelBox),
            Box(0f, -0.70f, 0.11f, 0.10f, 0.20f, 0.03f, Leather, shape: Panel),
            Box(0.07f, -0.92f, 0.11f, 0.04f, 0.05f, 0.04f, Bone, pitch: 90f, shape: Spike),
            Box(-0.07f, -0.92f, 0.11f, 0.04f, 0.05f, 0.04f, Bone, pitch: 90f, shape: Spike),
            Box(0.085f, -0.60f, -0.02f, 0.06f, 0.16f, 0.12f, Leather, roll: 24f, yaw: 180f, shape: Horn),
            Box(-0.085f, -0.60f, -0.02f, 0.06f, 0.16f, 0.12f, Leather, roll: -24f, yaw: 180f, shape: Horn),
            Box(0f, -0.84f, 0f, 0.26f, 0.04f, 0.26f, Leather, shape: Ring));

        Add("worn.hands.iron-gauntlets", ArmourMount.Arms,
            Box(0f, -0.76f, 0f, 0.26f, 0.44f, 0.26f, Iron, shape: Greave),
            Box(0f, -0.555f, 0f, 0.38f, 0.18f, 0.38f, Iron, shape: Flare),
            Box(0f, -0.455f, 0f, 0.26f, 0.035f, 0.26f, Steel, shape: Ring),
            Box(0f, -0.92f, 0.08f, 0.24f, 0.10f, 0.14f, Steel, shape: BevelBox),
            Box(0f, -0.66f, 0f, 0.28f, 0.05f, 0.28f, Iron, shape: BevelBox),
            Box(0f, -0.74f, 0f, 0.27f, 0.05f, 0.27f, Iron, shape: BevelBox),
            Box(0.07f, -0.95f, 0.10f, 0.04f, 0.07f, 0.04f, Steel, pitch: 90f, shape: Spike),
            Box(-0.07f, -0.95f, 0.10f, 0.04f, 0.07f, 0.04f, Steel, pitch: 90f, shape: Spike),
            Box(0.10f, -0.60f, -0.03f, 0.07f, 0.20f, 0.14f, Iron, roll: 28f, yaw: 180f, shape: Horn),
            Box(-0.10f, -0.60f, -0.03f, 0.07f, 0.20f, 0.14f, Iron, roll: -28f, yaw: 180f, shape: Horn),
            Box(0f, -0.83f, 0f, 0.28f, 0.04f, 0.28f, Gilt, shape: Ring));

        Add("worn.hands.plate-gauntlets", ArmourMount.Arms,
            Box(0f, -0.74f, 0f, 0.28f, 0.48f, 0.28f, Steel, shape: Greave),
            Box(0f, -0.80f, 0f, 0.32f, 0.06f, 0.32f, Iron, shape: BevelBox),
            Box(0f, -0.53f, 0f, 0.40f, 0.20f, 0.40f, Iron, shape: Flare),
            Box(0f, -0.43f, 0f, 0.38f, 0.045f, 0.38f, Iron, shape: Ring),
            Box(0f, -0.60f, 0f, 0.28f, 0.04f, 0.28f, Steel, shape: Ring),
            Box(0f, -0.95f, 0.06f, 0.22f, 0.08f, 0.20f, Iron, shape: BevelBox),
            Box(0f, -0.88f, 0.08f, 0.20f, 0.05f, 0.18f, Steel, shape: BevelBox),
            Box(0.08f, -0.98f, 0.08f, 0.04f, 0.08f, 0.04f, Steel, pitch: 90f, shape: Spike),
            Box(-0.08f, -0.98f, 0.08f, 0.04f, 0.08f, 0.04f, Steel, pitch: 90f, shape: Spike),
            Box(0.11f, -0.58f, -0.03f, 0.08f, 0.24f, 0.16f, Iron, roll: 26f, yaw: 180f, shape: Horn),
            Box(-0.11f, -0.58f, -0.03f, 0.08f, 0.24f, 0.16f, Iron, roll: -26f, yaw: 180f, shape: Horn),
            Box(0f, -0.70f, 0f, 0.30f, 0.04f, 0.30f, Gilt, shape: Ring));

        // The sorcerer's gauntlets: a fitted indigo shell that TRUMPETS twice, a gold bell at the elbow and a
        // second, smaller one rolled down over the wrist, and three gold forearm spikes of different lengths
        // swept up and back. The spikes are authored as MIRRORED PAIRS, not on one side: an arm model is drawn
        // once per side through CreateSideAttach, and a one-sided sculpt would lean the whole piece off the
        // arm's own axis. Their rolls are positive on +X because the yaw of 180 flips the roll's direction
        // (see the Horn note in the header).
        Add("worn.hands.sorcerer-gauntlets", ArmourMount.Arms,
            Box(0f, -0.75f, 0.01f, 0.26f, 0.46f, 0.28f, Indigo, shape: Greave),
            Box(0f, -0.555f, 0f, 0.38f, 0.18f, 0.38f, Gilt, shape: Flare),
            Box(0f, -0.90f, 0.02f, 0.34f, 0.14f, 0.34f, Gilt, pitch: 180f, shape: Flare),
            Box(0f, -0.955f, 0.06f, 0.22f, 0.09f, 0.22f, Indigo, shape: BevelBox),
            Box(0f, -0.92f, 0.14f, 0.18f, 0.04f, 0.04f, Steel, shape: BevelBox),
            Box(0.095f, -0.60f, -0.03f, 0.07f, 0.26f, 0.15f, Gilt, roll: 26f, yaw: 180f, shape: Horn),
            Box(-0.095f, -0.60f, -0.03f, 0.07f, 0.26f, 0.15f, Gilt, roll: -26f, yaw: 180f, shape: Horn),
            Box(0.10f, -0.70f, -0.03f, 0.06f, 0.20f, 0.13f, Gilt, roll: 22f, yaw: 180f, shape: Horn),
            Box(-0.10f, -0.70f, -0.03f, 0.06f, 0.20f, 0.13f, Gilt, roll: -22f, yaw: 180f, shape: Horn),
            Box(0.105f, -0.80f, -0.03f, 0.05f, 0.15f, 0.11f, Gilt, roll: 18f, yaw: 180f, shape: Horn),
            Box(-0.105f, -0.80f, -0.03f, 0.05f, 0.15f, 0.11f, Gilt, roll: -18f, yaw: 180f, shape: Horn));

        // ---- Legs (one leg): cover y -0.75..-0.10, |x| <= 0.11, |z| <= 0.11.
        //
        // A Ring lies in the XZ plane, so under a camera pitched 50 degrees down it presents its WHOLE TOP
        // FACE as a bright ellipse. Several light Rings down a leg stacked into what read as bandages, and
        // the legs are the lowest, nearest part of the figure, so they got the most of it. The rule now:
        // at most ONE light rim per leg piece (a knee or ankle accent, in Steel, kept narrow), every other
        // band is DarkIron or Gilt, or stops being a Ring and becomes a Taper lame that shows its SIDE instead.
        //
        // Pass 3 adds the flares: a leg piece FLARES at the thigh top and again just above the ankle, so the
        // limb is an hourglass too and not a tube, and carries a short horn each side at the thigh.

        Add("worn.legs.leather-leggings", ArmourMount.Legs,
            Box(0f, -0.43f, 0f, 0.30f, 0.68f, 0.30f, Leather, shape: Greave),
            Box(0f, -0.16f, 0f, 0.34f, 0.14f, 0.34f, Leather, shape: Flare),
            Box(0f, -0.45f, 0.14f, 0.16f, 0.08f, 0.16f, Iron, pitch: 90f, shape: Dome),
            Box(0f, -0.62f, 0f, 0.31f, 0.10f, 0.31f, Iron, pitch: 180f, shape: Taper),
            Box(0f, -0.50f, 0.15f, 0.04f, 0.30f, 0.03f, Leather, shape: Panel),
            Box(0f, -0.72f, 0f, 0.32f, 0.08f, 0.32f, Leather, pitch: 180f, shape: Flare),
            Box(0f, -0.555f, 0.16f, 0.07f, 0.05f, 0.05f, Steel, shape: BevelBox),
            Box(0f, -0.28f, 0f, 0.32f, 0.04f, 0.32f, Cold, shape: Ring),
            Box(0.09f, -0.19f, -0.02f, 0.05f, 0.14f, 0.10f, Leather, roll: 20f, yaw: 180f, shape: Horn),
            Box(-0.09f, -0.19f, -0.02f, 0.05f, 0.14f, 0.10f, Leather, roll: -20f, yaw: 180f, shape: Horn));

        Add("worn.legs.plated-leggings", ArmourMount.Legs,
            Box(0f, -0.43f, 0f, 0.30f, 0.68f, 0.30f, Cold, shape: Greave),
            Box(0f, -0.17f, 0f, 0.36f, 0.18f, 0.36f, Iron, shape: Flare),
            Box(0f, -0.32f, 0f, 0.34f, 0.22f, 0.34f, Iron, shape: Lame),
            Box(0f, -0.60f, 0.06f, 0.30f, 0.14f, 0.28f, Iron, shape: Greave),
            Box(0f, -0.72f, 0.03f, 0.32f, 0.10f, 0.30f, Iron, pitch: 180f, shape: Flare),
            Box(0f, -0.45f, 0.15f, 0.18f, 0.08f, 0.18f, Steel, pitch: 90f, shape: Dome),
            Box(0f, -0.52f, 0f, 0.33f, 0.04f, 0.33f, Iron, shape: Ring),
            Box(0f, -0.40f, 0.17f, 0.05f, 0.07f, 0.05f, Steel, pitch: 90f, shape: Spike),
            Box(0f, -0.345f, 0.13f, 0.18f, 0.03f, 0.14f, Steel, shape: Ring),
            Box(0.095f, -0.22f, -0.02f, 0.06f, 0.18f, 0.12f, Iron, roll: 22f, yaw: 180f, shape: Horn),
            Box(-0.095f, -0.22f, -0.02f, 0.06f, 0.18f, 0.12f, Iron, roll: -22f, yaw: 180f, shape: Horn),
            Box(0f, -0.665f, 0f, 0.31f, 0.035f, 0.31f, Gilt, shape: Ring));

        Add("worn.legs.chain-chausses", ArmourMount.Legs,
            Box(0f, -0.43f, 0f, 0.32f, 0.68f, 0.32f, Iron, shape: Greave),
            Box(0f, -0.14f, 0f, 0.36f, 0.08f, 0.36f, Iron, shape: Ring),
            Box(0f, -0.18f, 0f, 0.35f, 0.14f, 0.35f, Iron, shape: Flare),
            Box(0f, -0.73f, 0f, 0.26f, 0.04f, 0.26f, Steel, shape: Ring),
            Box(0f, -0.43f, 0.17f, 0.12f, 0.50f, 0.03f, Ash, shape: Panel),
            Box(0f, -0.30f, 0f, 0.34f, 0.04f, 0.34f, Iron, shape: Ring),
            Box(0f, -0.56f, 0f, 0.33f, 0.10f, 0.33f, Iron, pitch: 180f, shape: Taper),
            Box(0f, -0.45f, 0.15f, 0.16f, 0.06f, 0.16f, Steel, pitch: 90f, shape: Dome),
            Box(0f, -0.70f, 0f, 0.32f, 0.08f, 0.32f, Iron, pitch: 180f, shape: Flare),
            Box(0.09f, -0.20f, -0.02f, 0.05f, 0.16f, 0.10f, Iron, roll: 18f, yaw: 180f, shape: Horn),
            Box(-0.09f, -0.20f, -0.02f, 0.05f, 0.16f, 0.10f, Iron, roll: -18f, yaw: 180f, shape: Horn),
            Box(0f, -0.62f, 0f, 0.33f, 0.03f, 0.33f, Gilt, shape: Ring));

        Add("worn.legs.plate-greaves", ArmourMount.Legs,
            Box(0f, -0.50f, 0.03f, 0.36f, 0.50f, 0.36f, Iron, shape: Greave),
            Box(0f, -0.22f, 0f, 0.34f, 0.26f, 0.34f, Iron, shape: Lame),
            Box(0f, -0.14f, 0f, 0.37f, 0.14f, 0.37f, Iron, shape: Flare),
            Box(0f, -0.42f, 0.16f, 0.20f, 0.10f, 0.20f, Steel, pitch: 90f, shape: Dome),
            Box(0f, -0.58f, 0.18f, 0.05f, 0.26f, 0.04f, Steel, shape: BevelBox),
            Box(0f, -0.72f, 0f, 0.34f, 0.12f, 0.34f, Iron, pitch: 180f, shape: Flare),
            Box(0f, -0.665f, 0f, 0.26f, 0.04f, 0.26f, Steel, shape: Ring),
            Box(0f, -0.36f, 0.18f, 0.05f, 0.08f, 0.05f, Steel, pitch: 90f, shape: Spike),
            Box(0f, -0.30f, 0f, 0.35f, 0.035f, 0.35f, Gilt, shape: Ring),
            Box(0.10f, -0.17f, -0.02f, 0.06f, 0.20f, 0.12f, Iron, roll: 24f, yaw: 180f, shape: Horn),
            Box(-0.10f, -0.17f, -0.02f, 0.06f, 0.20f, 0.12f, Iron, roll: -24f, yaw: 180f, shape: Horn),
            Box(0f, -0.74f, 0.08f, 0.30f, 0.05f, 0.26f, Iron, shape: BevelBox));

        // The sorcerer's greaves, and the quiet piece of the set on purpose: the shoulders and the crown are the
        // statement, so the leg is a quilted cold-cloth thigh under a flared indigo cuff, a SLIM indigo greave
        // below it, and one ornament - a gold knee cop with a point on it. No horns down this limb and one
        // narrow Steel shin rib as its only bright fitting, because a leg is the lowest, nearest part of the
        // figure and anything light and level there stacks with the boot into a bandage.
        Add("worn.legs.sorcerer-greaves", ArmourMount.Legs,
            Box(0f, -0.28f, 0f, 0.33f, 0.38f, 0.33f, Cold, shape: Greave),
            Box(0f, -0.14f, 0f, 0.36f, 0.14f, 0.36f, Indigo, shape: Flare),
            Box(0f, -0.60f, 0.02f, 0.30f, 0.34f, 0.30f, Indigo, shape: Greave),
            Box(0f, -0.74f, 0.01f, 0.31f, 0.10f, 0.31f, Indigo, pitch: 180f, shape: Flare),
            Box(0f, -0.45f, 0.15f, 0.19f, 0.10f, 0.19f, Gilt, pitch: 90f, shape: Dome),
            Box(0f, -0.42f, 0.17f, 0.05f, 0.09f, 0.05f, Gilt, pitch: 90f, shape: Spike),
            Box(0f, -0.60f, 0.165f, 0.04f, 0.26f, 0.035f, Steel, shape: BevelBox),
            Box(0f, -0.33f, 0f, 0.34f, 0.035f, 0.34f, Gilt, shape: Ring));

        // ---- Feet (one leg): cover y -1.30..-0.88, |x| <= 0.132, z -0.05..+0.15.
        // The same rule as the legs: the boot's own bands used to stack on top of the leg bands into one
        // pale column, so a boot gets a single small light fitting (a buckle, a toe cap) and dark bands.
        // Pass 3: a boot FLARES at the top and again at the ankle, with a horn each side at the boot top,
        // so the lower leg ends in a trumpet rather than in a pipe.

        Add("worn.feet.leather-boots", ArmourMount.Feet,
            Box(0f, -1.09f, 0.07f, 0.28f, 0.44f, 0.34f, Leather, shape: Greave),
            Box(0f, -0.90f, 0.02f, 0.34f, 0.14f, 0.34f, Ash, shape: Flare),
            Box(0f, -0.83f, 0.02f, 0.30f, 0.04f, 0.30f, Steel, shape: Ring),
            Box(0f, -1.26f, 0.22f, 0.24f, 0.10f, 0.18f, Leather, pitch: 90f, shape: Dome),
            Box(0f, -1.30f, 0.08f, 0.28f, 0.05f, 0.40f, Ash, shape: BevelBox),
            Box(0f, -1.04f, 0.04f, 0.30f, 0.05f, 0.32f, Leather, shape: Ring),
            Box(0f, -1.14f, 0.03f, 0.32f, 0.10f, 0.33f, Leather, pitch: 180f, shape: Flare),
            Box(0f, -0.98f, 0.17f, 0.08f, 0.05f, 0.06f, Gilt, shape: BevelBox),
            Box(0.085f, -0.95f, -0.02f, 0.05f, 0.14f, 0.10f, Leather, roll: 20f, yaw: 180f, shape: Horn),
            Box(-0.085f, -0.95f, -0.02f, 0.05f, 0.14f, 0.10f, Leather, roll: -20f, yaw: 180f, shape: Horn));

        Add("worn.feet.iron-boots", ArmourMount.Feet,
            Box(0f, -1.09f, 0.07f, 0.30f, 0.44f, 0.34f, Iron, shape: Greave),
            Box(0f, -1.24f, 0.20f, 0.28f, 0.14f, 0.18f, Steel, pitch: 90f, shape: Dome),
            Box(0f, -0.90f, 0.03f, 0.36f, 0.16f, 0.36f, Iron, shape: Flare),
            Box(0f, -1.32f, 0.08f, 0.30f, 0.40f, 0.04f, Leather, pitch: 90f, shape: Panel),
            Box(0f, -0.96f, 0.19f, 0.09f, 0.06f, 0.07f, Steel, shape: BevelBox),
            Box(0f, -1.16f, -0.09f, 0.05f, 0.10f, 0.05f, Iron, pitch: -90f, shape: Spike),
            Box(0f, -1.12f, 0.04f, 0.33f, 0.10f, 0.33f, Iron, pitch: 180f, shape: Flare),
            Box(0f, -0.82f, 0.03f, 0.31f, 0.035f, 0.31f, Gilt, shape: Ring),
            Box(0.10f, -0.94f, -0.03f, 0.06f, 0.16f, 0.12f, Iron, roll: 22f, yaw: 180f, shape: Horn),
            Box(-0.10f, -0.94f, -0.03f, 0.06f, 0.16f, 0.12f, Iron, roll: -22f, yaw: 180f, shape: Horn),
            Box(0f, -1.04f, 0.05f, 0.32f, 0.05f, 0.32f, Iron, shape: Ring));

        Add("worn.feet.plate-sabatons", ArmourMount.Feet,
            Box(0f, -1.08f, 0.07f, 0.30f, 0.46f, 0.36f, Iron, shape: Greave),
            Box(0f, -1.31f, 0.12f, 0.30f, 0.05f, 0.46f, Iron, shape: BevelBox),
            Box(0f, -1.22f, 0.30f, 0.20f, 0.20f, 0.10f, Steel, pitch: 90f, shape: Taper),
            Box(0f, -0.98f, 0.03f, 0.34f, 0.06f, 0.36f, Iron, shape: BevelBox),
            Box(0f, -0.90f, 0.04f, 0.39f, 0.18f, 0.38f, Iron, shape: Flare),
            Box(0f, -1.14f, 0.05f, 0.35f, 0.12f, 0.37f, Iron, pitch: 180f, shape: Flare),
            Box(0f, -0.82f, 0.02f, 0.33f, 0.035f, 0.33f, Steel, shape: Ring),
            Box(0f, -1.20f, 0.38f, 0.05f, 0.07f, 0.05f, Steel, pitch: 90f, shape: Spike),
            Box(0.11f, -0.94f, -0.03f, 0.07f, 0.18f, 0.14f, Iron, roll: 24f, yaw: 180f, shape: Horn),
            Box(-0.11f, -0.94f, -0.03f, 0.07f, 0.18f, 0.14f, Iron, roll: -24f, yaw: 180f, shape: Horn),
            Box(0f, -1.05f, 0.05f, 0.33f, 0.05f, 0.35f, Iron, shape: Ring),
            Box(0f, -1.26f, 0.18f, 0.26f, 0.06f, 0.22f, Gilt, shape: BevelBox));

        // The sorcerer's sabatons: an indigo shell under a tall flared top and a second flare rolled down over
        // the ankle, and the piece's one piece of regalia, a LONG gold toe cap - a Taper pitched 90 so its
        // narrow end points forward, carried as far out as the foot's limit allows (z 0.41 of 0.42), which is
        // what gives the boot a pointed court silhouette instead of a square one. One small Steel buckle is its
        // only light fitting, for the same reason the greave has only a rib.
        Add("worn.feet.sorcerer-sabatons", ArmourMount.Feet,
            Box(0f, -1.08f, 0.06f, 0.30f, 0.46f, 0.34f, Indigo, shape: Greave),
            Box(0f, -0.90f, 0.055f, 0.38f, 0.18f, 0.37f, Indigo, shape: Flare),
            Box(0f, -1.14f, 0.04f, 0.34f, 0.12f, 0.35f, Indigo, pitch: 180f, shape: Flare),
            Box(0f, -1.23f, 0.26f, 0.20f, 0.26f, 0.11f, Gilt, pitch: 90f, shape: Taper),
            Box(0f, -1.31f, 0.10f, 0.30f, 0.05f, 0.44f, Indigo, shape: BevelBox),
            Box(0f, -0.98f, 0.18f, 0.09f, 0.05f, 0.05f, Steel, shape: BevelBox),
            Box(0f, -1.05f, 0.05f, 0.33f, 0.045f, 0.35f, Gilt, shape: Ring),
            Box(0f, -0.82f, 0.02f, 0.33f, 0.035f, 0.33f, Gilt, shape: Ring),
            Box(0f, -1.20f, -0.06f, 0.24f, 0.16f, 0.10f, Indigo, shape: BevelBox));

        // ---- Back (torso frame): the torso back surface is at z >= -0.14 and the panels hang off it at
        // z -0.22..-0.34. Above y 0.30 nothing is wider than 1.6x the torso (0.416); the hem may flare to 0.50.
        //
        // PASS 4: A CLOAK FRAMES THE FIGURE, IT DOES NOT REPLACE IT. In the engine the pass-3 cloaks read as
        // large flat dark panels crossing in FRONT of the legs - separate props hanging off the figure rather
        // than cloth on it - and they filled the waist back in, which cost the hourglass the body pieces had
        // just been given. Three changes, all of them subtraction:
        //   narrower  - every fall lost 0.04..0.10 of width, so the legs show between and outside them;
        //   further back - the tiers moved from -0.19..-0.28 to -0.22..-0.34, a full panel thickness behind
        //     where they were, so under a camera looking down and forward they sit behind the leg, not on it;
        //   shorter - no fall now reaches the knee (y -0.43). The longest, the hooded cloak, hems at about
        //     -0.44; the travel cloak and the mantle stop at the hip.
        // The cloaks keep their order - hooded longest, mantle broadest, travel shortest - because that is
        // what tells them apart at 28 px; they just stop being the whole lower silhouette.

        // Every cloak is a TAPERED STACK of Panels, never one plank: narrow at the shoulders (<= 0.44), widest
        // near the hem, each tier 0.03 further back (half the 0.06 thickness, so neighbouring tiers do not
        // interpenetrate) and pitched a little more than the one above (positive pitch swings the bottom edge
        // backwards), so the lengths take different light and the hem hangs away from the body. Hem panels are
        // inset or split into tabs so the bottom outline is not a square line. A Panel is itself curved and
        // wavy, which is where cloth now gets its run of light; the clasps and collars are hard shapes against it.

        // Short and ragged: a leather yoke with a clasp, a narrow upper back and FOUR falls hung in pairs,
        // each yawed away from the next so no two take the same light. A single wide tier (this one was
        // 0.70 across) reads as a plank nailed to the figure however well it is pitched: the cure is to
        // split it, turn the halves, and let daylight between them. Hip length: the falls end at about
        // y -0.08, well above the knee, so the whole leg is the figure's own.
        Add("worn.back.travel-cloak", ArmourMount.Back,
            Box(0f, 0.84f, -0.22f, 0.38f, 0.10f, 0.06f, Leather, shape: Panel),
            Box(0f, 0.74f, -0.22f, 0.30f, 0.05f, 0.06f, Leather, shape: BevelBox),
            Box(0f, 0.56f, -0.26f, 0.30f, 0.36f, 0.06f, Ash, pitch: 3f, shape: Panel),
            Box(0.20f, 0.26f, -0.30f, 0.20f, 0.40f, 0.06f, Ash, pitch: 6f, yaw: 18f, shape: Panel),
            Box(-0.20f, 0.26f, -0.30f, 0.20f, 0.40f, 0.06f, Cold, pitch: 6f, yaw: -18f, shape: Panel),
            Box(0.155f, 0.06f, -0.34f, 0.18f, 0.28f, 0.06f, Ash, pitch: 10f, yaw: 12f, shape: Panel),
            Box(-0.155f, 0.10f, -0.34f, 0.18f, 0.20f, 0.06f, Cold, pitch: 10f, yaw: -12f, shape: Panel),
            Box(0f, 0.90f, -0.17f, 0.10f, 0.04f, 0.08f, Steel, shape: BevelBox),
            Box(0f, 0.90f, -0.14f, 0.07f, 0.07f, 0.07f, Gilt, pitch: 90f, shape: Ring));

        // The long one - to just above the knee, not to the ankle - hung as SIX narrow lengths in mirrored
        // pairs rather than as three broad tiers: the wide tiers (0.56 and 0.70 across) buried the legs in one
        // flat dark slab that had no curvature to show, and the ankle-length version of those lengths crossed
        // in front of the shins. Each pair is yawed apart, each length of a pair is cut to a different height,
        // so the hem steps instead of running straight across and the legs show between and below the falls.
        // Above it all a raised standing collar (not a hood) with a bone rim, and a clasp bar at the throat.
        Add("worn.back.hooded-cloak", ArmourMount.Back,
            Box(0f, 0.62f, -0.22f, 0.32f, 0.60f, 0.06f, Ash, pitch: 2f, shape: Panel),
            Box(0.185f, 0.12f, -0.26f, 0.22f, 0.44f, 0.06f, Cold, pitch: 5f, yaw: 16f, shape: Panel),
            Box(-0.185f, 0.12f, -0.26f, 0.22f, 0.44f, 0.06f, Ash, pitch: 5f, yaw: -16f, shape: Panel),
            Box(0.16f, -0.18f, -0.30f, 0.20f, 0.42f, 0.06f, Ash, pitch: 8f, yaw: 12f, shape: Panel),
            Box(-0.16f, -0.10f, -0.30f, 0.20f, 0.34f, 0.06f, Cold, pitch: 8f, yaw: -12f, shape: Panel),
            Box(0.125f, -0.25f, -0.34f, 0.17f, 0.36f, 0.06f, Ash, pitch: 12f, yaw: 8f, shape: Panel),
            Box(-0.125f, -0.30f, -0.34f, 0.17f, 0.28f, 0.06f, Cold, pitch: 12f, yaw: -8f, shape: Panel),
            Box(0f, 1.00f, -0.22f, 0.34f, 0.30f, 0.06f, Ash, pitch: -10f, shape: Panel),
            Box(0f, 1.175f, -0.22f, 0.24f, 0.03f, 0.06f, Bone, shape: Ring),
            Box(0f, 0.86f, -0.17f, 0.26f, 0.05f, 0.07f, Iron, shape: BevelBox));

        // A broad shoulder mantle, layered and drooping at the points with iron barbs, over a short tapered
        // back panel. The mantle is the BROAD one, so its falls are the shortest of the three: the character
        // is all at the shoulders and the hem stops at the hip rather than competing with them.
        Add("worn.back.grave-mantle", ArmourMount.Back,
            Box(0f, 0.84f, -0.08f, 0.50f, 0.40f, 0.10f, Cold, pitch: -90f, shape: Pauldron),
            Box(0.28f, 0.80f, -0.08f, 0.24f, 0.10f, 0.40f, Cold, roll: -18f, shape: Panel),
            Box(-0.28f, 0.80f, -0.08f, 0.24f, 0.10f, 0.40f, Cold, roll: 18f, shape: Panel),
            Box(0f, 0.66f, -0.10f, 0.66f, 0.10f, 0.36f, Ash, shape: Panel),
            Box(0f, 0.905f, -0.08f, 0.26f, 0.03f, 0.22f, Bone, shape: Ring),
            Box(0f, 0.40f, -0.22f, 0.32f, 0.44f, 0.06f, Cold, pitch: 3f, shape: Panel),
            Box(-0.175f, 0.08f, -0.26f, 0.24f, 0.36f, 0.06f, Ash, pitch: 6f, yaw: -14f, shape: Panel),
            Box(0.185f, 0.12f, -0.30f, 0.26f, 0.28f, 0.06f, Cold, pitch: 8f, yaw: 14f, shape: Panel),
            Box(0.34f, 0.74f, -0.06f, 0.07f, 0.07f, 0.07f, Iron, roll: -40f, shape: Spike),
            Box(-0.34f, 0.74f, -0.06f, 0.07f, 0.07f, 0.07f, Iron, roll: 40f, shape: Spike));

        // The sorcerer's mantle: the STRAIGHT-CUT one. Where the travel cloak is ragged and the grave mantle is
        // broad, this one is a narrow court mantle - an indigo plate yoke at the shoulders over three tiers of
        // cold cloth that barely widen as they fall - finished with a gold border across its bottom edge, which
        // is what makes it read as regalia rather than as a blanket. It is the longest fall of the four back
        // items after the hooded cloak and still stops ABOVE the knee: the design asks for calf length, and the
        // back-slot contract (nothing past y -0.43, nothing in front of the legs) is what it actually gets, for
        // the reason pass 4 recorded - a fall that reaches the shin projects across the leg at this camera and
        // the cloak stops being cloth on a figure.
        Add("worn.back.sorcerer-mantle", ArmourMount.Back,
            Box(0f, 0.86f, -0.12f, 0.40f, 0.34f, 0.09f, Indigo, pitch: -90f, shape: Pauldron),
            Box(0f, 0.88f, -0.16f, 0.22f, 0.04f, 0.07f, Steel, shape: BevelBox),
            Box(0f, 0.72f, -0.22f, 0.30f, 0.44f, 0.06f, Cold, pitch: 2f, shape: Panel),
            Box(0.135f, 0.30f, -0.28f, 0.24f, 0.46f, 0.06f, Cold, pitch: 5f, yaw: 8f, shape: Panel),
            Box(-0.135f, 0.30f, -0.28f, 0.24f, 0.46f, 0.06f, Cold, pitch: 5f, yaw: -8f, shape: Panel),
            Box(0.115f, -0.14f, -0.34f, 0.21f, 0.42f, 0.06f, Cold, pitch: 8f, yaw: 5f, shape: Panel),
            Box(-0.115f, -0.14f, -0.34f, 0.21f, 0.42f, 0.06f, Cold, pitch: 8f, yaw: -5f, shape: Panel),
            Box(0f, -0.30f, -0.31f, 0.44f, 0.05f, 0.06f, Gilt, shape: Panel),
            Box(0f, 0.79f, -0.19f, 0.26f, 0.03f, 0.06f, Gilt, shape: Ring));

        // Not cloth, and not two flat violet quads either: a DARK IRON SPAR FRAME - a long leading spar laid
        // along the top of each wing and a shorter rib under it - carrying a smaller arcane membrane inside.
        // The membrane is the smaller area, the frame is what gives the wing an outline, and the two wings
        // are yawed apart (not merely rolled) so they face different ways and take visibly different light
        // instead of reading as one saturated tone. Bone is kept for the barbed tips only.
        Add("worn.back.gossamer-wings", ArmourMount.Back,
            Box(0.345f, 0.705f, -0.30f, 0.26f, 0.42f, 0.05f, ArmourMaterial.Arcane, roll: -14f, yaw: 20f, shape: Membrane),
            Box(-0.345f, 0.705f, -0.30f, 0.26f, 0.42f, 0.05f, ArmourMaterial.Arcane, roll: 14f, yaw: -20f, shape: Membrane),
            Box(0.33f, 0.88f, -0.31f, 0.05f, 0.46f, 0.04f, Iron, roll: -70f, shape: Shaft),
            Box(-0.33f, 0.88f, -0.31f, 0.05f, 0.46f, 0.04f, Iron, roll: 70f, shape: Shaft),
            Box(0.30f, 0.62f, -0.30f, 0.04f, 0.40f, 0.04f, Iron, roll: -40f, shape: Shaft),
            Box(-0.30f, 0.62f, -0.30f, 0.04f, 0.40f, 0.04f, Iron, roll: 40f, shape: Shaft),
            Box(0f, 0.62f, -0.28f, 0.20f, 0.20f, 0.06f, Cold, shape: Lame),
            Box(0.46f, 0.94f, -0.31f, 0.05f, 0.08f, 0.05f, Bone, roll: -20f, shape: Spike),
            Box(-0.46f, 0.94f, -0.31f, 0.05f, 0.08f, 0.05f, Bone, roll: 20f, shape: Spike));

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
