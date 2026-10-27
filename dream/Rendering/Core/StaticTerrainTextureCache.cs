using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Core;

// Terrain textures decode on the thread pool and upload on the render thread. GetOrLoad returns
// -1 (draw untextured) while a decode is pending, uploads the decoded pixels the next time it is
// called for that key, and from then on returns the cached handle. A missing file or a failed
// decode or upload caches -1 for good, so a broken texture is never retried every frame.
public sealed class StaticTerrainTextureCache : IDisposable
{
    public const string FaultSource = "static-terrain-texture";

    private readonly Dictionary<StaticTerrainTextureKey, int> _textures = new();
    private readonly Dictionary<StaticTerrainTextureKey, Task<DecodedTexture?>> _pending = new();
    private readonly Func<string, DecodedTexture?> _decode;
    private readonly Func<DecodedTexture, int> _upload;
    private readonly Action<int> _deleteTexture;
    private readonly IRenderFaultSink _faultSink;
    private bool _disposed;

    public StaticTerrainTextureCache()
        : this(DecodeFile, UploadTexture, DeleteTexture, RenderFaultLog.Shared)
    {
    }

    // Test seam: decode runs inside Task.Run, upload and deleteTexture run on the caller's thread.
    public StaticTerrainTextureCache(
        Func<string, DecodedTexture?> decode,
        Func<DecodedTexture, int> upload,
        Action<int> deleteTexture,
        IRenderFaultSink faultSink)
    {
        ArgumentNullException.ThrowIfNull(decode);
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(deleteTexture);
        ArgumentNullException.ThrowIfNull(faultSink);
        _decode = decode;
        _upload = upload;
        _deleteTexture = deleteTexture;
        _faultSink = faultSink;
    }

    public int PendingCount => _pending.Count;

    public int GetOrLoad(StaticTerrainMaterial material)
    {
        if (_disposed || !material.HasTexture)
        {
            return -1;
        }

        StaticTerrainTextureKey key = new(material.MaterialId, material.Role);
        if (_textures.TryGetValue(key, out int existing))
        {
            return existing;
        }

        if (!_pending.TryGetValue(key, out Task<DecodedTexture?>? pending))
        {
            string path = material.TexturePath;
            _pending[key] = Task.Run(() => _decode(path));
            return -1;
        }

        Task<DecodedTexture?>? completed = pending;
        if (BackgroundWork.TryTakeCompleted<DecodedTexture?>(ref completed, out DecodedTexture? decoded, out Exception? fault))
        {
            _pending.Remove(key);
            int created = Upload(decoded);
            _textures[key] = created;
            return created;
        }

        if (fault != null)
        {
            _pending.Remove(key);
            _faultSink.Report(FaultSource, fault);
            _textures[key] = -1;
        }

        return -1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Task<DecodedTexture?> pending in _pending.Values)
        {
            BackgroundWork.ObserveAndForget(pending, _faultSink, FaultSource);
        }

        foreach (int texture in _textures.Values)
        {
            if (texture != -1)
            {
                _deleteTexture(texture);
            }
        }

        _pending.Clear();
        _textures.Clear();
    }

    private int Upload(DecodedTexture? decoded)
    {
        if (decoded == null)
        {
            return -1;
        }

        try
        {
            return _upload(decoded);
        }
        catch (Exception exception)
        {
            _faultSink.Report(FaultSource, exception);
            return -1;
        }
    }

    // Null for a missing file, which the cache stores as the untextured fallback.
    private static DecodedTexture? DecodeFile(string path) =>
        File.Exists(path) ? TextureDecoder.DecodeRgbaFlipped(path) : null;

    private static int UploadTexture(DecodedTexture texture) =>
        TextureDecoder.UploadTexture2D(texture, generateMipmaps: true);

    private static void DeleteTexture(int texture) => GL.DeleteTexture(texture);

    private readonly record struct StaticTerrainTextureKey(
        GeometryMaterialId MaterialId,
        StaticTerrainMaterialRole Role);
}
