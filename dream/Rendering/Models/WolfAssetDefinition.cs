using System.IO;
using OpenTK.Mathematics;
using Dream.Assets;
using Dream.Simulation.Enemies;

namespace Dream.Rendering.Models;


public static class WolfAssetDefinition
{
    public static string ModelPath => AssetPaths.Animation(Path.Combine("enemies", "wolf.glb"));
    public static WorldAssetNormalization Normalization { get; } = new(3.191f, 0.60f);

    public const string IdleClip = "AnimalArmature|Idle";

    // The pack really carries two gaits and a death clip; the prefixed names are the ones to keep
    // (bare-name duplicates exist in the same file). WalkClip plays at the patrol speed, GallopClip
    // at the chase speed, and DieClip is what a corpse holds - it was aliased to the idle for a
    // wave because nobody had read the pack's animation list.
    public const string WalkClip = "AnimalArmature|Walk";
    public const string GallopClip = "AnimalArmature|Gallop";
    public const string DieClip = "AnimalArmature|Death";

    // The original name of the walk, kept so every existing caller still compiles.
    public const string MoveClip = WalkClip;
    public const string AttackClip = "AnimalArmature|Attack";
    public const float CombatAnchorHeight = WolfDefinition.CombatAnchorHeight;
    public const float PreferredStandOffDistance = WolfDefinition.PreferredStandOffDistance;
    public const float CollisionRadius = WolfDefinition.CollisionRadius;
    public const float CollisionHeight = WolfDefinition.CollisionHeight;
    public const float PlayerSeparationDistance = WolfDefinition.PlayerSeparationDistance;

    public static Vector3 AuthoredForwardAxis => Vector3.UnitZ;

    public static float VisualScale => Normalization.ResolveUniformScale();
}
