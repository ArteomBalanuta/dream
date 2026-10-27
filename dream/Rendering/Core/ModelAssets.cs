using Dream.Rendering.Models;
using Dream.Simulation.Players;

namespace Dream.Rendering.Core;

// The game's model cache. Models decode on the thread pool and upload on the render thread in
// PumpUploads, which PlayScene calls once per frame before drawing; nothing here loads a model
// synchronously. Callers poll TryGet (shared models) or a handle from RequestPlayerInstance
// (per-instance player models) and skip drawing until the model is ready.
//
// Ownership:
// - Shared models (cubes, wolf, viewmodels) belong to the loader. Callers only borrow them for a
//   draw and never dispose them.
// - A handle from RequestPlayerInstance belongs to the caller, which disposes it when the actor
//   leaves or the renderer is disposed. Disposing a handle is idempotent.
// - DisposeAll disposes the loader: every shared model, and any instance handle still alive (so a
//   renderer that was not disposed cannot leak GL). A handle its owner already disposed is skipped,
//   and a handle disposed after DisposeAll does nothing, so nothing is disposed twice. A fresh
//   loader replaces the old one, so a later scene loads the models again.
//
// The blocking Get(path) was removed: after DEC-5 every draw path polls instead, and nothing
// needs a model synchronously.
public static class ModelAssets
{
    public const string FaultSource = "model-assets";

    // Resolved once (AssetPaths combines on every read; the runtime root does not change), so
    // per-draw lookups and the cube-scale comparison do not allocate.
    public static readonly string GrassCubePath = GrassCubeAssetDefinition.ModelPath;
    public static readonly string DirtCubePath = DirtCubeAssetDefinition.ModelPath;
    public static readonly string StoneCubePath = StoneCubeAssetDefinition.ModelPath;
    public static readonly string WolfPath = WolfAssetDefinition.ModelPath;
    public static readonly string PlayerPath = PlayerModelAssetDefinition.ModelPath;

    private static AsyncResourceLoader<AnimatedModelData, AnimatedModel> _loader = CreateLoader();

    private static AsyncResourceLoader<AnimatedModelData, AnimatedModel> Loader => Volatile.Read(ref _loader);

    // Requests the model if needed and returns it once uploaded. Never blocks.
    public static bool TryGet(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AnimatedModel? model)
    {
        AsyncResourceLoader<AnimatedModelData, AnimatedModel> loader = Loader;
        if (loader.TryGet(path, out model))
        {
            return true;
        }

        loader.Request(path);
        return false;
    }

    public static bool IsFailed(string path) => Loader.IsFailed(path);

    // Starts the background decodes so the models are usually ready by the first draw.
    public static void Preload(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        AsyncResourceLoader<AnimatedModelData, AnimatedModel> loader = Loader;
        foreach (string path in paths)
        {
            loader.Request(path);
        }
    }

    // Render thread only, once per frame before drawing.
    public static int PumpUploads(int maxPerFrame) => Loader.PumpUploads(maxPerFrame);

    // A player model with its own pose; each call decodes the file again. The caller owns the
    // handle and must dispose it.
    public static AsyncResourceHandle<AnimatedModel> RequestPlayerInstance() =>
        Loader.RequestInstance(PlayerPath);

    /// <summary>
    /// Releases every shared model and every per-instance model still alive, and starts a fresh
    /// loader. Only valid at scene teardown, after the last draw that uses them.
    /// </summary>
    public static void DisposeAll()
    {
        AsyncResourceLoader<AnimatedModelData, AnimatedModel> previous = Interlocked.Exchange(ref _loader, CreateLoader());
        previous.Dispose();
    }

    private static AsyncResourceLoader<AnimatedModelData, AnimatedModel> CreateLoader() =>
        new(AnimatedModelData.Load, static data => new AnimatedModel(data), RenderFaultLog.Shared, FaultSource);
}
