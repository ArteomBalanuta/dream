using System.IO;
using OpenTK.Mathematics;

public static class WolfAssetDefinition
{
    public static string ModelPath => AssetPaths.Animation(Path.Combine("enemies", "wolf.glb"));
    public static WorldAssetNormalization Normalization { get; } = new(3.191f, 1.20f);

    public const string IdleClip = "AnimalArmature|Idle";
    public const string MoveClip = "AnimalArmature|Walk";
    public const string AttackClip = "AnimalArmature|Attack";
    public const float CombatAnchorHeight = 0.75f;
    public const float PreferredStandOffDistance = 2.00f;
    public const float MinimumVisualDistanceFromCamera = 2.75f;

    public static Vector3 AuthoredForwardAxis => Vector3.UnitZ;

    public static float VisualScale => Normalization.ResolveUniformScale();
}
