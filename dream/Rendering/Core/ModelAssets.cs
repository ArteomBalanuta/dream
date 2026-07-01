using Dream.Assets;
using Dream.Rendering.Models;
using Dream.Simulation.Players;

namespace Dream.Rendering.Core;

public static class ModelAssets
{
    private static readonly ResourceCache<AnimatedModel> Cache = new();

    public static AnimatedModel DebugHands =>
        Get(AssetPaths.Animation("viewmodels/DebugHands.gltf"));

    public static AnimatedModel DebugHeldItem =>
        Get(AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"));

    public static AnimatedModel GrassCube =>
        Get(AssetPaths.Animation("GrassTextureCube.gltf"));

    public static AnimatedModel Wolf =>
        Get(WolfAssetDefinition.ModelPath);

    public static AnimatedModel Player =>
        Get(PlayerModelAssetDefinition.ModelPath);

    public static AnimatedModel CreatePlayerInstance() =>
        new(PlayerModelAssetDefinition.ModelPath);

    public static AnimatedModel Get(string path) =>
        Cache.GetOrAdd(path, modelPath => new AnimatedModel(modelPath));
}
