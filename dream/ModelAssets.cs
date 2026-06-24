public static class ModelAssets
{
    private static readonly ResourceCache<AnimatedModel> Cache = new();

    public static AnimatedModel GrassCube =>
        Cache.GetOrAdd(AssetPaths.Animation("GrassTextureCube.gltf"), path => new AnimatedModel(path));
}
