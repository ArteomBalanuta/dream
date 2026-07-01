namespace Dream.Rendering.Models;

public readonly record struct WorldAssetDimensions(
    float Height,
    float Width,
    float Depth)
{
    public static WorldAssetDimensions UnitCube => new(1f, 1f, 1f);

    public bool HasPositiveVolume =>
        Height > 0f &&
        Width > 0f &&
        Depth > 0f;
}
