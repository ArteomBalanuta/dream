using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;

// How a material of a held or worn piece answers light, beyond its albedo.
//
// Until now every equipment box was drawn with metallic 0 and roughness 1 through a purely diffuse
// actor shader, so blackened iron, oiled leather and polished steel differed only in how dark they
// were. These four numbers are handed to enemyFragment.glsl as `uEquipSurface` and drive a
// specular lobe, a fresnel edge sheen and a procedural surface grain that only the equipment path
// ever switches on: with the uniform at zero (every other actor, enemy, NPC and prop draw) the
// shader computes exactly what it computed before, line for line.
//
//   Metallic  0..1  how much of the specular takes the albedo's colour rather than the light's, and
//                   how strong the fresnel edge sheen is. Metal ~1, leather and cloth 0.
//   Roughness 0..1  the width of the specular lobe. Polished steel ~0.28, blackened iron ~0.45,
//                   oiled leather ~0.6, cloth ~0.9. Never 0: a mirror lobe aliases at 28 px.
//   Grain     0..1  the amplitude of the procedural surface grain (hammer dimples on plate, weave on
//                   cloth, pitting on old iron). Modulates albedo and roughness, never the normal:
//                   a normal perturbation at this pixel size is noise, a value perturbation is wear.
//   AccentMix 0..1  how much of an item's accent tint this material takes (see EquipmentTint). The
//                   trim materials take it fully; the structural metal takes none, so a tier colour
//                   reads as a trim, not as a wash over the whole piece.
// The procedural surface a material wears, mirrored by the constants at the top of the equipment
// block in enemyFragment.glsl. A part is one flat colour without this: sculpting fixed the FORM,
// and this is what fixes the SURFACE - panel seams and rivets on plate, fibre and stitching on
// leather, weave and folds on cloth, striations on bone, long grain on wood, filigree on a glowing
// piece. The pattern only ever takes light away; anything that has to read bright does it by
// lowering local roughness, so it glints instead of breaking the albedo ceiling.
public enum EquipmentPattern
{
    None = 0,
    PolishedPlate,
    WornPlate,
    Hide,
    Weave,
    Bone,
    Wood,
    Rune,
}

public readonly record struct EquipmentSurface(
    float Metallic,
    float Roughness,
    float Grain,
    float AccentMix,
    EquipmentPattern Pattern = EquipmentPattern.None,
    float PatternDepth = 0f)
{
    // The shader off. Any draw that does not set uEquipSurface leaves it here.
    public static readonly EquipmentSurface None = new(0f, 0f, 0f, 0f);

    // x metallic, y roughness, z grain, w accentMix - the layout enemyFragment.glsl reads.
    public Vector4 ToUniform() => new(Metallic, Roughness, Grain, AccentMix);

    // The second uniform the shader reads: x pattern id, y the part's size in METRES (so a cuff and
    // a cuirass get the same real grain size rather than the same number of cycles across very
    // different objects), z the pattern depth, w a per-item seed.
    public Vector4 ToDetailUniform(float partMetres, float seed) =>
        new((float)Pattern, MathF.Max(partMetres, 0.001f), PatternDepth, seed);

    // Roughness is clamped away from a mirror in the shader too; this is the authoring floor.
    public const float MinRoughness = 0.18f;

    // The table is tuned so a material can be NAMED from a 20 px patch, which means the axes have to
    // be far apart, not nudged: the shader's rim strength is 0.06 + 0.70 * Metallic * (1 - Roughness),
    // so bright plate (Steel/Gilt, rim ~0.58) outlines hard, blackened iron (~0.34) softly, lacquer
    // (Indigo ~0.33 at metallic 0.55) in between, and leather, bone, wood and cloth (<= 0.1) barely.
    // Roughness separates the dielectrics: bone 0.50 is waxy and takes a glint, leather 0.66 a dull
    // sheen, wood 0.84 and cloth 0.96 none. Grain is what makes a patch look worn: low on polished
    // steel and lacquer, high on iron, leather, cloth and bone.
    public static EquipmentSurface For(WeaponMaterial material) => material switch
    {
        WeaponMaterial.Steel => new EquipmentSurface(1.0f, 0.26f, 0.18f, 0.0f, EquipmentPattern.PolishedPlate, 0.85f),
        WeaponMaterial.DarkIron => new EquipmentSurface(0.85f, 0.54f, 0.62f, 0.0f, EquipmentPattern.WornPlate, 1.0f),
        WeaponMaterial.Wood => new EquipmentSurface(0.0f, 0.84f, 0.70f, 0.1f, EquipmentPattern.Wood, 0.95f),
        WeaponMaterial.Leather => new EquipmentSurface(0.05f, 0.66f, 0.60f, 0.25f, EquipmentPattern.Hide, 0.95f),
        WeaponMaterial.Gilt => new EquipmentSurface(1.0f, 0.30f, 0.22f, 1.0f, EquipmentPattern.PolishedPlate, 0.70f),
        WeaponMaterial.Cloth => new EquipmentSurface(0.0f, 0.96f, 0.85f, 1.0f, EquipmentPattern.Weave, 0.90f),
        WeaponMaterial.Arcane => new EquipmentSurface(0.35f, 0.22f, 0.15f, 1.0f, EquipmentPattern.Rune, 0.80f),
        WeaponMaterial.Ember => new EquipmentSurface(0.2f, 0.55f, 0.60f, 1.0f, EquipmentPattern.Rune, 0.70f),
        WeaponMaterial.Bone => new EquipmentSurface(0.0f, 0.50f, 0.62f, 0.2f, EquipmentPattern.Bone, 0.90f),
        _ => None,
    };

    public static EquipmentSurface For(ArmourMaterial material) => material switch
    {
        ArmourMaterial.Steel => new EquipmentSurface(1.0f, 0.26f, 0.18f, 0.0f, EquipmentPattern.PolishedPlate, 0.85f),
        ArmourMaterial.DarkIron => new EquipmentSurface(0.85f, 0.54f, 0.62f, 0.0f, EquipmentPattern.WornPlate, 1.0f),
        ArmourMaterial.Leather => new EquipmentSurface(0.05f, 0.66f, 0.60f, 0.25f, EquipmentPattern.Hide, 0.95f),
        ArmourMaterial.ClothCold => new EquipmentSurface(0.0f, 0.96f, 0.85f, 1.0f, EquipmentPattern.Weave, 0.90f),
        ArmourMaterial.ClothAsh => new EquipmentSurface(0.0f, 0.96f, 0.85f, 1.0f, EquipmentPattern.Weave, 0.95f),
        ArmourMaterial.ClothRed => new EquipmentSurface(0.0f, 0.96f, 0.85f, 1.0f, EquipmentPattern.Weave, 0.90f),
        ArmourMaterial.Bone => new EquipmentSurface(0.0f, 0.50f, 0.62f, 0.2f, EquipmentPattern.Bone, 0.90f),
        ArmourMaterial.Gilt => new EquipmentSurface(1.0f, 0.30f, 0.22f, 1.0f, EquipmentPattern.PolishedPlate, 0.70f),
        ArmourMaterial.Arcane => new EquipmentSurface(0.35f, 0.22f, 0.15f, 1.0f, EquipmentPattern.Rune, 0.80f),
        ArmourMaterial.Ember => new EquipmentSurface(0.2f, 0.55f, 0.60f, 1.0f, EquipmentPattern.Rune, 0.70f),
        // Lacquered plate: metallic and fairly smooth, so a torch finds it, with the plate pattern
        // and a low grain because enamel does not pit the way bare iron does. Takes the accent tint
        // fully, so a tier reads on the field as well as on the trim.
        ArmourMaterial.Indigo => new EquipmentSurface(0.55f, 0.30f, 0.20f, 1.0f, EquipmentPattern.PolishedPlate, 0.80f),
        _ => None,
    };
}

// The two numbers the surface pattern needs that are not properties of the material: how big the
// part really is, and which item it belongs to.
public static class EquipmentDetail
{
    // 1 m = 1.408 model units on the authored figure (HeldItemScale).
    public const float ModelUnitsPerMetre = 1.408f;

    // The mean authored extent of a part, in metres. Without it a buckle and a breastplate would get
    // the same NUMBER of grain cycles rather than the same grain SIZE, and every small part would
    // look like coarse rock.
    public static float PartMetres(Vector3 size, float itemScale) =>
        (size.X + size.Y + size.Z) / 3f * itemScale / ModelUnitsPerMetre;

    // A stable seed in 0..1 from the visual id (FNV-1a), so two pieces of the same material do not
    // share a wear map, and one item looks the same every frame, on every client, forever.
    public static float SeedFor(string? visualId)
    {
        if (string.IsNullOrEmpty(visualId))
        {
            return 0f;
        }

        uint hash = 2166136261u;
        for (int i = 0; i < visualId.Length; i++)
        {
            hash = (hash ^ visualId[i]) * 16777619u;
        }

        return (hash & 0xFFFFu) / 65535f;
    }
}

// The colour variation an item carries: a primary multiplier over the whole piece and an accent
// multiplier that only the trim materials take (EquipmentSurface.AccentMix). This is the hook dyes
// and rarity tiers hang on. It is applied on the CPU, to the part's baseColorFactor, before the
// draw - NOT in the shader - for one reason: the world pass has no tonemap, so albedo is bound by
// MaxAlbedoChannel and that bound has to be enforceable by a test, not hoped for in GLSL.
public readonly record struct EquipmentTint(Vector3 Primary, Vector3 Accent)
{
    // The albedo ceiling the whole actor palette lives under: the frame gain reaches about 2.2 beside
    // a brazier, and the world pass clamps with no tonemap, so anything past this clips to a flat
    // patch. Apply() can therefore never brighten a piece into a blown-out block.
    public const float MaxAlbedoChannel = 0.42f;

    public static readonly EquipmentTint Neutral = new(Vector3.One, Vector3.One);

    public bool IsNeutral => Primary == Vector3.One && Accent == Vector3.One;

    // baseColor times the tint this material takes, clamped per channel to MaxAlbedoChannel. Alpha
    // is untouched.
    public Vector4 Apply(Vector4 baseColor, float accentMix)
    {
        float mix = Math.Clamp(accentMix, 0f, 1f);
        Vector3 factor = (Primary * (1f - mix)) + (Accent * mix);
        return new Vector4(
            MathF.Min(baseColor.X * factor.X, MaxAlbedoChannel),
            MathF.Min(baseColor.Y * factor.Y, MaxAlbedoChannel),
            MathF.Min(baseColor.Z * factor.Z, MaxAlbedoChannel),
            baseColor.W);
    }
}
