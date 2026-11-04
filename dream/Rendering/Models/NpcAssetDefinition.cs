using System.IO;
using OpenTK.Mathematics;
using Dream.Assets;

namespace Dream.Rendering.Models;

// The forged village NPCs: rigid part hierarchies (contract C1 node names), two clips each. They
// replace the two coloured boxes WorldEntityRenderer used to emit and stand at the player's world
// height rather than the boxes' 1.64 m. Each model measures its own authored bounds at runtime,
// like PlayerModelAssetDefinition and BeastAssetDefinition: no hard-coded authored constant.
public static class NpcAssetDefinition
{
    public const string IdleClip = "idle";
    public const string WalkClip = "walk";

    // Combined on every AssetPaths read and the runtime root does not change, so resolve once.
    private static readonly string VendorPathValue = ResolvePath("villager-vendor.glb");
    private static readonly string SmithPathValue = ResolvePath("villager-smith.glb");
    private static readonly string WardenPathValue = ResolvePath("villager-warden.glb");

    private static readonly Lazy<WorldAssetNormalization> VendorNormalizationCache =
        new(() => ForgedAssetBounds.ResolveNormalization(VendorPathValue, TargetWorldHeight));

    private static readonly Lazy<WorldAssetNormalization> SmithNormalizationCache =
        new(() => ForgedAssetBounds.ResolveNormalization(SmithPathValue, TargetWorldHeight));

    private static readonly Lazy<WorldAssetNormalization> WardenNormalizationCache =
        new(() => ForgedAssetBounds.ResolveNormalization(WardenPathValue, TargetWorldHeight));

    public static string VendorModelPath => VendorPathValue;

    public static string SmithModelPath => SmithPathValue;

    public static string WardenModelPath => WardenPathValue;

    // Villagers match the player (a wave decision), not the 1.64 m boxes they replace.
    public static float TargetWorldHeight => PlayerModelAssetDefinition.TargetWorldDimensions.Height;

    public static WorldAssetNormalization VendorNormalization => VendorNormalizationCache.Value;

    public static WorldAssetNormalization SmithNormalization => SmithNormalizationCache.Value;

    public static WorldAssetNormalization WardenNormalization => WardenNormalizationCache.Value;

    // Measured on first use and cached for the process.
    public static float VendorVisualScale => VendorNormalization.ResolveUniformScale();

    public static float SmithVisualScale => SmithNormalization.ResolveUniformScale();

    public static float WardenVisualScale => WardenNormalization.ResolveUniformScale();

    public static Vector3 AuthoredForwardAxis => Vector3.UnitZ;

    // The models' origin is at the feet, like the villager positions the server replicates, so an
    // NPC needs no body-centre correction (the player's position is its body centre; see
    // PlayerModelAssetDefinition.BodyCenterToFootOffset).
    public static Vector3 BodyCenterToFootOffset => Vector3.Zero;

    private static string ResolvePath(string fileName) =>
        AssetPaths.Animation(Path.Combine("npcs", fileName));
}
