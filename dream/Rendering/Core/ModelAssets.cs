using System.Diagnostics.CodeAnalysis;
using Dream.Rendering.Models;
using Dream.Simulation.Players;

namespace Dream.Rendering.Core;

// The game's model cache. Models decode on the thread pool and upload on the render thread in
// PumpUploads, which PlayScene calls once per frame before drawing; nothing here loads a model
// synchronously. Callers poll TryGet (shared models) or a handle from RequestPlayerInstance
// (a player's own animation instance) and skip drawing until the model is ready.
//
// Ownership:
// - Every model (cubes, wolf, player, viewmodels) is decoded and uploaded once and belongs to the
//   loader. Callers only borrow it for a draw and never dispose it. Actors never animate the
//   shared model itself: each owns an AnimatedModelInstance over its skeleton.
// - A handle from RequestPlayerInstance belongs to the caller, which disposes it when the actor
//   leaves or the renderer is disposed. It holds the actor's AnimatedModelInstance over the shared
//   player model; it owns no GL resources, and disposing it is idempotent.
// - DisposeAll disposes the loader: every shared model, exactly once. Handles from that loader
//   then report Disposed and draw nothing; disposing them afterwards does nothing. A fresh
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

    // The forged actors of the models-and-rigs wave. These are paths only: nothing here reads the
    // GLB, so a path whose asset is not shipped yet costs one failed background decode (logged by
    // RenderFaultLog) and draws nothing.
    public static readonly string HoundPath = BeastAssetDefinition.HoundModelPath;
    public static readonly string SkeletonPath = SkeletonAssetDefinition.ModelPath;
    public static readonly string NpcVendorPath = NpcAssetDefinition.VendorModelPath;
    public static readonly string NpcSmithPath = NpcAssetDefinition.SmithModelPath;
    public static readonly string NpcWardenPath = NpcAssetDefinition.WardenModelPath;

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

    // Decodes not uploaded yet, shared and per-instance (what the loading screen waits for).
    public static int PendingCount => Loader.PendingCount;

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

    // A player's own animation instance over the shared player model, which is decoded and
    // uploaded once for every player. The caller owns the handle and must dispose it.
    public static AnimatedModelInstanceHandle RequestPlayerInstance() =>
        new(Loader, PlayerPath);

    /// <summary>
    /// Releases every shared model (instances own no GL resources, so there is nothing else to
    /// release) and starts a fresh
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

// One actor's animation instance over a shared ModelAssets model. The model is requested from the
// loader (one decode and one upload, however many handles exist); the instance is created on the
// first TryGet after the model is uploaded and kept for the handle's life. The handle owns no GL
// resources: the model stays the loader's. Render thread only.
public sealed class AnimatedModelInstanceHandle : IDisposable
{
    private readonly AsyncResourceLoader<AnimatedModelData, AnimatedModel> _loader;
    private AnimatedModelInstance? _instance;
    private bool _disposed;

    internal AnimatedModelInstanceHandle(AsyncResourceLoader<AnimatedModelData, AnimatedModel> loader, string path)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _loader = loader;
        Path = path;
        loader.Request(path);
    }

    public string Path { get; }

    // The shared model's state, or Disposed once this handle or its loader was disposed.
    public AsyncResourceState State =>
        _disposed || _loader.IsDisposed ? AsyncResourceState.Disposed : _loader.GetState(Path);

    public bool IsReady => State == AsyncResourceState.Ready;

    // The shared model and this actor's instance, once the model is uploaded. Allocates only the
    // first time it succeeds (the instance).
    public bool TryGet(
        [NotNullWhen(true)] out AnimatedModel? model,
        [NotNullWhen(true)] out AnimatedModelInstance? instance)
    {
        instance = null;
        if (_disposed || _loader.IsDisposed || !_loader.TryGet(Path, out model))
        {
            model = null;
            return false;
        }

        if (_instance is null || !ReferenceEquals(_instance.Skeleton, model.Skeleton))
        {
            _instance = model.CreateInstance();
        }

        instance = _instance;
        return true;
    }

    // Drops the instance; the shared model is the loader's and is never disposed here.
    public void Dispose()
    {
        _disposed = true;
        _instance = null;
    }
}
