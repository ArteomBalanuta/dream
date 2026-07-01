using OpenTK.Mathematics;
using Dream.Assets;

namespace Dream.Rendering.Models;

public static class DirtCubeAssetDefinition
{
    private static readonly Lazy<WorldAssetDimensions> AuthoredDimensionsCache =
        new(() => WorldAssetBounds.MeasureDimensions(ModelPath));

    public static string ModelPath => AssetPaths.Animation("DirtTextureCube.gltf");

    public static Vector3 AuthoredForwardAxis => Vector3.UnitZ;

    public static WorldAssetDimensions AuthoredDimensions => AuthoredDimensionsCache.Value;

    public static WorldAssetDimensions TargetWorldDimensions => new(
        Height: 1f,
        Width: 1f,
        Depth: 1f);

    public static WorldAssetNormalization Normalization { get; } = new(
        AuthoredDimensions,
        TargetWorldDimensions);

    public static float VisualScale => Normalization.ResolveUniformScale();
}
