using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Core;

// The eight blendable terrain tiles as one 2D array texture (layer = (int)GeometryMaterialId), for
// the terrain shader's cross-material height blend. Same seam shape as StaticTerrainTextureCache:
// the 8 decodes run on the thread pool, GetOrLoad returns -1 until every layer is uploaded (the
// shader then falls back to the per-material texture), and uploads happen on the caller's thread,
// at most one layer per call. Any missing file, decode or upload failure, or a tile that is not
// 256x256 caches -1 for good. The array is allocated once, with its full mip chain, when the first
// decode completes; nothing is allocated per frame.
public sealed class StaticTerrainTileArray : IDisposable
{
    public const string FaultSource = "static-terrain-tile-array";
    public const int LayerCount = 8;
    public const int TileSize = 256;
    private const int MipCount = 9; // 256 -> 1

    private static readonly string[] LayerPathTable = CreateLayerPaths();

    private readonly Task<DecodedTexture?>?[] _pending = new Task<DecodedTexture?>?[LayerCount];
    private readonly DecodedTexture?[] _decoded = new DecodedTexture?[LayerCount];
    private readonly bool[] _uploaded = new bool[LayerCount];
    private readonly Func<string, DecodedTexture?> _decode;
    private readonly Func<int> _allocate;
    private readonly Action<int, int, DecodedTexture> _uploadLayer;
    private readonly Action<int> _deleteTexture;
    private readonly IRenderFaultSink _faultSink;
    private int _handle = -1;
    private int _uploadedCount;
    private bool _started;
    private bool _failed;
    private bool _disposed;

    public StaticTerrainTileArray()
        : this(DecodeFile, AllocateArray, UploadLayer, DeleteArray, RenderFaultLog.Shared)
    {
    }

    // Test seam: decode runs inside Task.Run; allocate, uploadLayer(handle, layer, pixels) and
    // deleteTexture run on the caller's thread.
    public StaticTerrainTileArray(
        Func<string, DecodedTexture?> decode,
        Func<int> allocate,
        Action<int, int, DecodedTexture> uploadLayer,
        Action<int> deleteTexture,
        IRenderFaultSink faultSink)
    {
        ArgumentNullException.ThrowIfNull(decode);
        ArgumentNullException.ThrowIfNull(allocate);
        ArgumentNullException.ThrowIfNull(uploadLayer);
        ArgumentNullException.ThrowIfNull(deleteTexture);
        ArgumentNullException.ThrowIfNull(faultSink);
        _decode = decode;
        _allocate = allocate;
        _uploadLayer = uploadLayer;
        _deleteTexture = deleteTexture;
        _faultSink = faultSink;
    }

    // Tile file per layer, in GeometryMaterialId order (grass uses its top tile).
    public static IReadOnlyList<string> LayerPaths => LayerPathTable;

    public bool IsReady => !_failed && !_disposed && _uploadedCount == LayerCount;

    public bool HasFailed => _failed;

    // The array texture handle once every layer is uploaded, otherwise -1.
    public int GetOrLoad()
    {
        if (_disposed || _failed)
        {
            return -1;
        }

        if (_uploadedCount == LayerCount)
        {
            return _handle;
        }

        if (!_started)
        {
            _started = true;
            for (int layer = 0; layer < LayerCount; layer++)
            {
                string path = LayerPathTable[layer];
                _pending[layer] = Task.Run(() => _decode(path));
            }

            return -1;
        }

        for (int layer = 0; layer < LayerCount; layer++)
        {
            if (_pending[layer] == null)
            {
                continue;
            }

            // The type argument is spelled out so the nullable decode result matches the task's own
            // DecodedTexture?; a null decode is expected and handled right below.
            if (BackgroundWork.TryTakeCompleted<DecodedTexture?>(ref _pending[layer], out DecodedTexture? decoded, out Exception? fault))
            {
                if (decoded == null || decoded.Width != TileSize || decoded.Height != TileSize ||
                    decoded.Rgba.Length != 4 * TileSize * TileSize)
                {
                    Fail(new InvalidOperationException(
                        $"Terrain tile '{LayerPathTable[layer]}' is missing or not {TileSize}x{TileSize} RGBA."));
                    return -1;
                }

                _decoded[layer] = decoded;
            }
            else if (fault != null)
            {
                Fail(fault);
                return -1;
            }
        }

        if (_handle == -1 && Array.Exists(_decoded, static tile => tile != null))
        {
            try
            {
                _handle = _allocate();
            }
            catch (Exception exception)
            {
                Fail(exception);
                return -1;
            }
        }

        for (int layer = 0; layer < LayerCount; layer++)
        {
            DecodedTexture? tile = _decoded[layer];
            if (tile == null || _uploaded[layer])
            {
                continue;
            }

            try
            {
                _uploadLayer(_handle, layer, tile);
            }
            catch (Exception exception)
            {
                Fail(exception);
                return -1;
            }

            _uploaded[layer] = true;
            _decoded[layer] = null;
            _uploadedCount++;
            break;
        }

        return _uploadedCount == LayerCount ? _handle : -1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ObservePending();
        DeleteHandle();
        Array.Clear(_decoded);
    }

    private void Fail(Exception fault)
    {
        _failed = true;
        _faultSink.Report(FaultSource, fault);
        ObservePending();
        Array.Clear(_decoded);
        DeleteHandle();
    }

    private void ObservePending()
    {
        for (int layer = 0; layer < LayerCount; layer++)
        {
            BackgroundWork.ObserveAndForget(_pending[layer], _faultSink, FaultSource);
            _pending[layer] = null;
        }
    }

    private void DeleteHandle()
    {
        if (_handle == -1)
        {
            return;
        }

        int handle = _handle;
        _handle = -1;
        try
        {
            _deleteTexture(handle);
        }
        catch (Exception exception)
        {
            _faultSink.Report(FaultSource, exception);
        }
    }

    private static string[] CreateLayerPaths()
    {
        string[] paths = new string[LayerCount];
        for (int layer = 0; layer < LayerCount; layer++)
        {
            GeometryMaterialId id = (GeometryMaterialId)layer;
            paths[layer] = StaticTerrainMaterialCatalog.Resolve(
                id,
                id == GeometryMaterialId.Grass ? StaticTerrainMaterialRole.Top : StaticTerrainMaterialRole.Single).TexturePath;
        }

        return paths;
    }

    private static DecodedTexture? DecodeFile(string path) =>
        File.Exists(path) ? TextureDecoder.DecodeRgbaFlipped(path) : null;

    // Storage for every mip level; Repeat wrap (one repeat per 1 m cell) and trilinear filtering.
    // Uses unit 2 (the terrain tile unit) to bind and leaves unit 0 active.
    private static int AllocateArray()
    {
        GLUtils.AssertRenderThread("StaticTerrainTileArray.Allocate");
        int handle = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2DArray, handle);
        int size = TileSize;
        for (int level = 0; level < MipCount; level++)
        {
            GL.TexImage3D(
                TextureTarget.Texture2DArray,
                level,
                PixelInternalFormat.Rgba8,
                size,
                size,
                LayerCount,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                IntPtr.Zero);
            size = Math.Max(1, size / 2);
        }

        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureBaseLevel, 0);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMaxLevel, MipCount - 1);
        GL.BindTexture(TextureTarget.Texture2DArray, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        return handle;
    }

    // One layer, all mip levels, with the same 2x2 box reduction as the 2D terrain textures.
    private static void UploadLayer(int handle, int layer, DecodedTexture tile)
    {
        GLUtils.AssertRenderThread("StaticTerrainTileArray.UploadLayer");
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2DArray, handle);
        byte[] level = tile.Rgba;
        int size = TileSize;
        for (int mip = 0; mip < MipCount; mip++)
        {
            GL.TexSubImage3D(
                TextureTarget.Texture2DArray,
                mip,
                0,
                0,
                layer,
                size,
                size,
                1,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                level);
            if (size > 1)
            {
                level = TextureDecoder.BoxReduce(level, size, size);
                size /= 2;
            }
        }

        GL.BindTexture(TextureTarget.Texture2DArray, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    private static void DeleteArray(int handle) => GL.DeleteTexture(handle);
}
