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
public readonly record struct EquipmentSurface(float Metallic, float Roughness, float Grain, float AccentMix)
{
    // The shader off. Any draw that does not set uEquipSurface leaves it here.
    public static readonly EquipmentSurface None = new(0f, 0f, 0f, 0f);

    // x metallic, y roughness, z grain, w accentMix - the layout enemyFragment.glsl reads.
    public Vector4 ToUniform() => new(Metallic, Roughness, Grain, AccentMix);

    // Roughness is clamped away from a mirror in the shader too; this is the authoring floor.
    public const float MinRoughness = 0.18f;

    public static EquipmentSurface For(WeaponMaterial material) => material switch
    {
        WeaponMaterial.Steel => new EquipmentSurface(1.0f, 0.30f, 0.30f, 0.0f),
        WeaponMaterial.DarkIron => new EquipmentSurface(0.9f, 0.48f, 0.55f, 0.0f),
        WeaponMaterial.Wood => new EquipmentSurface(0.0f, 0.78f, 0.70f, 0.1f),
        WeaponMaterial.Leather => new EquipmentSurface(0.1f, 0.62f, 0.65f, 0.25f),
        WeaponMaterial.Gilt => new EquipmentSurface(1.0f, 0.34f, 0.35f, 1.0f),
        WeaponMaterial.Cloth => new EquipmentSurface(0.0f, 0.90f, 0.80f, 1.0f),
        WeaponMaterial.Arcane => new EquipmentSurface(0.35f, 0.22f, 0.15f, 1.0f),
        WeaponMaterial.Ember => new EquipmentSurface(0.2f, 0.55f, 0.60f, 1.0f),
        WeaponMaterial.Bone => new EquipmentSurface(0.0f, 0.66f, 0.72f, 0.2f),
        _ => None,
    };

    public static EquipmentSurface For(ArmourMaterial material) => material switch
    {
        ArmourMaterial.Steel => new EquipmentSurface(1.0f, 0.32f, 0.32f, 0.0f),
        ArmourMaterial.DarkIron => new EquipmentSurface(0.9f, 0.50f, 0.58f, 0.0f),
        ArmourMaterial.Leather => new EquipmentSurface(0.1f, 0.62f, 0.65f, 0.25f),
        ArmourMaterial.ClothCold => new EquipmentSurface(0.0f, 0.92f, 0.80f, 1.0f),
        ArmourMaterial.ClothAsh => new EquipmentSurface(0.0f, 0.90f, 0.82f, 1.0f),
        ArmourMaterial.ClothRed => new EquipmentSurface(0.0f, 0.88f, 0.78f, 1.0f),
        ArmourMaterial.Bone => new EquipmentSurface(0.0f, 0.66f, 0.72f, 0.2f),
        ArmourMaterial.Gilt => new EquipmentSurface(1.0f, 0.34f, 0.35f, 1.0f),
        ArmourMaterial.Arcane => new EquipmentSurface(0.35f, 0.22f, 0.15f, 1.0f),
        ArmourMaterial.Ember => new EquipmentSurface(0.2f, 0.55f, 0.60f, 1.0f),
        _ => None,
    };
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
