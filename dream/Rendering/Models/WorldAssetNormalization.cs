namespace Dream.Rendering.Models;

public readonly record struct WorldAssetNormalization(
    float AuthoredHeight,
    float TargetWorldHeight)
{
    public float ResolveUniformScale()
    {
        if (AuthoredHeight <= 0f)
        {
            return 1f;
        }

        return TargetWorldHeight / AuthoredHeight;
    }
}
