using System.IO;
using OpenTK.Mathematics;

public static class PlayerModelAssetDefinition
{
    public static string ModelPath => AssetPaths.Animation(Path.Combine("players", "kenney-blocky-character-a.glb"));

    public const string IdleClip = "idle";
    public const string WalkClip = "walk";
    public const string HeadNodeName = "head";

    public static Vector3 AuthoredForwardAxis => -Vector3.UnitZ;

    public static float VisualScale => 0.85f;

    public static Vector3 BodyCenterToFootOffset => new(0f, -0.5f, 0f);
}
