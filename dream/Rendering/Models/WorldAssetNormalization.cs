namespace Dream.Rendering.Models;

public readonly record struct WorldAssetNormalization(
    float AuthoredHeight,
    float TargetWorldHeight)
{
    public WorldAssetNormalization(
        WorldAssetDimensions authoredDimensions,
        WorldAssetDimensions targetDimensions)
        : this(authoredDimensions.Height, targetDimensions.Height)
    {
        AuthoredDimensions = authoredDimensions;
        TargetDimensions = targetDimensions;
    }

    public WorldAssetDimensions? AuthoredDimensions { get; }

    public WorldAssetDimensions? TargetDimensions { get; }

    public float ResolveUniformScale()
    {
        if (AuthoredHeight <= 0f)
        {
            return 1f;
        }

        return TargetWorldHeight / AuthoredHeight;
    }

    public WorldAssetDimensions ResolveScaledDimensions()
    {
        if (AuthoredDimensions is not WorldAssetDimensions authored)
        {
            return new WorldAssetDimensions(TargetWorldHeight, TargetWorldHeight, TargetWorldHeight);
        }

        float scale = ResolveUniformScale();
        return new WorldAssetDimensions(
            authored.Height * scale,
            authored.Width * scale,
            authored.Depth * scale);
    }
}
