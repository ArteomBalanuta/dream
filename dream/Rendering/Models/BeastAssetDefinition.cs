using System.IO;
using OpenTK.Mathematics;
using Dream.Assets;

namespace Dream.Rendering.Models;

// The forged skinned beasts of the models-and-rigs wave; the hound is the first. Unlike
// WolfAssetDefinition, whose authored height is the hard-coded 3.191f of a third-party pack, a
// forged beast MEASURES its own authored bounds at runtime the way PlayerModelAssetDefinition
// does, so re-forging the asset cannot silently change the world height it draws at. Clip names
// are bare, as the forge writes them (the wolf's are "AnimalArmature|...").
public static class BeastAssetDefinition
{
    public const string IdleClip = "idle";
    public const string MoveClip = "walk";
    public const string AttackClip = "attack";
    public const string DieClip = "die";

    // Combined on every AssetPaths read and the runtime root does not change, so resolve once.
    private static readonly string HoundPathValue = AssetPaths.Animation(Path.Combine("enemies", "hound.glb"));

    private static readonly Lazy<WorldAssetNormalization> HoundNormalizationCache =
        new(() => ForgedAssetBounds.ResolveNormalization(HoundPathValue, TargetWorldHeight));

    public static string HoundModelPath => HoundPathValue;

    // Beasts stand at the wolf's world height, so the hound and the wolf stay interchangeable on
    // an enemy template while the wolf is kept as the fallback.
    public static float TargetWorldHeight => WolfAssetDefinition.Normalization.TargetWorldHeight;

    public static WorldAssetNormalization HoundNormalization => HoundNormalizationCache.Value;

    // Measured on first use and cached for the process.
    public static float HoundVisualScale => HoundNormalization.ResolveUniformScale();

    public static Vector3 AuthoredForwardAxis => Vector3.UnitZ;
}

// The authored bounds of a forged GLB, measured once. A missing or unreadable asset is not a
// render-loop exception: the measurement falls back to a zero-height WorldAssetDimensions, which
// WorldAssetNormalization already resolves to a uniform scale of 1, so the actor draws at its
// authored size (or not at all, since ModelAssets.TryGet also fails) instead of throwing inside a
// draw. Shared by the beast and NPC definitions, which both measure instead of hard-coding.
internal static class ForgedAssetBounds
{
    internal static WorldAssetNormalization ResolveNormalization(string modelPath, float targetWorldHeight) =>
        new(
            TryMeasureDimensions(modelPath),
            new WorldAssetDimensions(targetWorldHeight, 1f, 1f));

    internal static WorldAssetDimensions TryMeasureDimensions(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            return default;
        }

        try
        {
            return WorldAssetBounds.MeasureDimensions(modelPath);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return default;
        }
    }
}
