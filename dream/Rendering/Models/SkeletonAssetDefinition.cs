using System.IO;
using OpenTK.Mathematics;
using Dream.Assets;

namespace Dream.Rendering.Models;

// The forged risen skeleton, a skinned biped. Like BeastAssetDefinition it MEASURES its own authored
// bounds at runtime (ForgedAssetBounds), so re-forging the asset cannot silently change the world
// height it draws at. Unlike the beasts it is a biped, so its target height is the player's, not the
// wolf's. Clip names are bare, as the forge writes them. The blade is part of the mesh (a skinned
// actor carries no pose-layer contract), and `walk` doubles as the sprint clip: there is one gait.
public static class SkeletonAssetDefinition
{
    public const string IdleClip = "idle";
    public const string MoveClip = "walk";
    public const string AttackClip = "attack";
    public const string DieClip = "die";

    // Combined on every AssetPaths read and the runtime root does not change, so resolve once.
    private static readonly string ModelPathValue = AssetPaths.Animation(Path.Combine("enemies", "skeleton.glb"));

    private static readonly Lazy<WorldAssetNormalization> NormalizationCache =
        new(() => ForgedAssetBounds.ResolveNormalization(ModelPathValue, TargetWorldHeight));

    public static string ModelPath => ModelPathValue;

    // A biped stands at the player's world height (PlayerModelAssetDefinition: twice the wolf's).
    public static float TargetWorldHeight => PlayerModelAssetDefinition.TargetWorldDimensions.Height;

    public static WorldAssetNormalization Normalization => NormalizationCache.Value;

    // Measured on first use and cached for the process.
    public static float VisualScale => Normalization.ResolveUniformScale();

    public static Vector3 AuthoredForwardAxis => Vector3.UnitZ;
}
