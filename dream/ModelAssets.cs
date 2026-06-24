public static class ModelAssets
{
    private static readonly ResourceCache<AnimatedModel> Cache = new();

    public static AnimatedModel DebugHands =>
        Get(AssetPaths.Animation("viewmodels/DebugHands.gltf"));

    public static AnimatedModel DebugHeldItem =>
        Get(AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"));

    public static AnimatedModel GrassCube =>
        Get(AssetPaths.Animation("GrassTextureCube.gltf"));

    public static AnimatedModel Get(string path) =>
        Cache.GetOrAdd(path, modelPath => new AnimatedModel(modelPath));
}
