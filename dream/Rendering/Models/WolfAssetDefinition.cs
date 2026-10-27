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
    public const string MoveClip = "AnimalArmature|Walk";
    public const string AttackClip = "AnimalArmature|Attack";
    public const float CombatAnchorHeight = WolfDefinition.CombatAnchorHeight;
    public const float PreferredStandOffDistance = WolfDefinition.PreferredStandOffDistance;
    public const float CollisionRadius = WolfDefinition.CollisionRadius;
    public const float CollisionHeight = WolfDefinition.CollisionHeight;
    public const float PlayerSeparationDistance = WolfDefinition.PlayerSeparationDistance;

    public static Vector3 AuthoredForwardAxis => Vector3.UnitZ;

    public static float VisualScale => Normalization.ResolveUniformScale();
}
