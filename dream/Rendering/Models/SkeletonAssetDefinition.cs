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

    // The ground speed the `walk` clip was AUTHORED for, in world units per second - the speed at
    // which it plays at rate 1.0 without the feet skating. It is a property of the clip, not of the
    // enemy: the Risen Skeleton chases at 3.6, which is 4.3x this, so during a chase the stride sits
    // at EnemyClipSet.MaxPlaybackSpeed and the feet do skate. That is the clamp working as designed
    // (the hound's gallop hits the opposite clamp for the same reason), and it is why this number
    // must never be set from the template's chase speed - doing so once already put two different
    // references on one clip and halved the stride at the gait latch.
    //
    // Derived in tools/ui-assets/gen_char_skeleton.py, whose WALK_STRIDE_U and WALK_CYCLE_S state it:
    // each foot is planted for half of a 0.88 s cycle while the leg swings +-20 degrees about the hip,
    // so a full stride is 2 * 2 * (6.61 + 6.41) * sin(20 deg) = 17.812 u. The figure is 29.136 u tall
    // and normalises to TargetWorldHeight, giving 0.041186 world units per u, so the stride is 0.7336
    // world units and the speed is 0.7336 / 0.88. At the shipped patrol speed of 1.25 this plays at
    // 1.50 and the feet match the ground exactly.
    public const float WalkAuthoredGroundSpeed = 0.8337f;

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
