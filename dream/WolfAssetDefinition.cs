using System.IO;

public static class WolfAssetDefinition
{
    public static string ModelPath => AssetPaths.Animation(Path.Combine("enemies", "wolf.glb"));
    public static WorldAssetNormalization Normalization { get; } = new(3.191f, 1.20f);

    public const string IdleClip = "AnimalArmature|Idle";
    public const string MoveClip = "AnimalArmature|Walk";
    public const string AttackClip = "AnimalArmature|Attack";
    public const float CombatAnchorHeight = 0.75f;

    public static float VisualScale => Normalization.ResolveUniformScale();
}
