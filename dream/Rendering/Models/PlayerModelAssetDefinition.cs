using System.IO;
using OpenTK.Mathematics;
using Dream.Assets;

namespace Dream.Rendering.Models;


public static class PlayerModelAssetDefinition
{
    private static readonly Lazy<WorldAssetDimensions> AuthoredDimensionsCache =
        new(() => WorldAssetBounds.MeasureDimensions(ModelPath));

    public static string ModelPath => AssetPaths.Animation(Path.Combine("players", "player-grim.glb"));

    public const string IdleClip = "idle";
    public const string WalkClip = "walk";
    public const string HeadNodeName = "head";

    public static Vector3 AuthoredForwardAxis => Vector3.UnitZ;

    public static WorldAssetDimensions AuthoredDimensions => AuthoredDimensionsCache.Value;

    public static WorldAssetDimensions TargetWorldDimensions => new(
        Height: WolfAssetDefinition.Normalization.TargetWorldHeight * 2f,
        Width: 1.0f,
        Depth: 1.0f);

    public static WorldAssetNormalization Normalization { get; } = new(
        AuthoredDimensions,
        TargetWorldDimensions);

    public static float VisualScale => Normalization.ResolveUniformScale();

    public static Vector3 BodyCenterToFootOffset => new(0f, -0.5f, 0f);
}
