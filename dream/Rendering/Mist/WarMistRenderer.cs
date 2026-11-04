using Dream.Assets;
using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Mist;

// The war mist pass: N horizontal slices over the map in one instanced draw of a four-vertex
// strip (corners from gl_VertexID, heights from a uniform table, no mesh VBO), premultiplied-alpha
// blended over the finished world. Depth is tested but not written, so terrain and props that rise
// through a slice cut it, and the pass needs no sorting and no offscreen target. One program bind,
// one optional texture upload and a fixed set of uniform uploads per frame, through locations
// cached at construction; nothing is allocated per frame.
public sealed class WarMistRenderer : IDisposable
{
    public const string VertexShaderFile = "warMistVertex.glsl";
    public const string FragmentShaderFile = "warMistFragment.glsl";

    // Every uniform the war mist shaders declare, in the order of the Location* indices below. A
    // test checks this list against the shader sources, so a new uniform cannot go unset.
    public static readonly string[] UniformNames =
    [
        "uViewProjection",
        "uExtentMin",
        "uExtentMax",
        "uSliceHeights",
        "uNoise",
        "uExploration",
        "uFocus",
        "uMapSize",
        "uSliceOffsets",
        "uSliceCount",
    ];

    private const int LocationViewProjection = 0;
    private const int LocationExtentMin = 1;
    private const int LocationExtentMax = 2;
    private const int LocationSliceHeights = 3;
    private const int LocationNoise = 4;
    private const int LocationExploration = 5;
    private const int LocationFocus = 6;
    private const int LocationMapSize = 7;
    private const int LocationSliceOffsets = 8;
    private const int LocationSliceCount = 9;

    private readonly int[] _locations = new int[UniformNames.Length];
    private readonly float[] _sliceHeights;
    private readonly float[] _sliceOffsets = new float[WarMistSettings.MaxSlices * 4];
    private readonly int _sliceCount;
    private readonly ExplorationTexture _exploration = new();
    private Shader? _shader;
    private int _vertexArray;
    private int _noise;

    public WarMistRenderer(int sliceCount)
    {
        GLUtils.AssertRenderThread("War mist renderer creation");
        _sliceCount = Math.Clamp(sliceCount, WarMistSettings.MinSlices, WarMistSettings.MaxSlices);
        _sliceHeights = WarMistField.CreateSliceHeights(_sliceCount);
        try
        {
            _shader = new Shader(AssetPaths.Shader(VertexShaderFile), AssetPaths.Shader(FragmentShaderFile));
            ReadOnlySpan<int> locations = _shader.GetUniformLocations(UniformNames);
            locations.CopyTo(_locations);
            _noise = WarMistNoiseTexture.Upload(WarMistNoiseTexture.Generate());
            // Core profile draws need a bound VAO even when no attribute is read.
            _vertexArray = GL.GenVertexArray();
            GLUtils.CheckGLError("War mist renderer creation");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsDisposed => _shader == null;

    // Draws the slices around the focus over the world and restores the world pass state afterwards:
    // depth writes on, blending off, face culling as it was, no VAO, texture units 0 and 1 empty.
    public void Draw(Matrix4 viewProjection, Vector3 focus, double timeSeconds, ExplorationMemory memory)
    {
        Shader shader = _shader ?? throw new ObjectDisposedException(nameof(WarMistRenderer));

        // The window is not clipped to the map: past the edge the slices carry on over the void, so
        // the veil has no visible rim there (the exploration texture clamps to its edge texel).
        float minX = focus.X - WarMistField.HalfExtent;
        float minZ = focus.Z - WarMistField.HalfExtent;
        float maxX = focus.X + WarMistField.HalfExtent;
        float maxZ = focus.Z + WarMistField.HalfExtent;

        bool cullWasEnabled = GL.IsEnabled(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        if (cullWasEnabled)
        {
            GL.Disable(EnableCap.CullFace);
        }

        shader.Use();
        shader.SetMatrix4(_locations[LocationViewProjection], viewProjection);
        Upload(_locations[LocationExtentMin], minX, minZ);
        Upload(_locations[LocationExtentMax], maxX, maxZ);
        if (_locations[LocationSliceHeights] >= 0)
        {
            GL.Uniform1(_locations[LocationSliceHeights], _sliceHeights.Length, _sliceHeights);
            RenderCounters.CountUniformUpload();
        }

        Upload(_locations[LocationFocus], focus.X, focus.Z);
        Upload(_locations[LocationMapSize], memory.Width, memory.Depth);
        WarMistField.SliceOffsets(timeSeconds, _sliceCount, _sliceOffsets);
        if (_locations[LocationSliceOffsets] >= 0)
        {
            GL.Uniform4(_locations[LocationSliceOffsets], WarMistSettings.MaxSlices, _sliceOffsets);
            RenderCounters.CountUniformUpload();
        }

        Upload(_locations[LocationSliceCount], (float)_sliceCount);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _noise);
        Upload(_locations[LocationNoise], 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        _exploration.Bind(memory);
        Upload(_locations[LocationExploration], 1);

        GL.BindVertexArray(_vertexArray);
        GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, _sliceCount);
        RenderCounters.CountDraw();
        GL.BindVertexArray(0);

        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        if (cullWasEnabled)
        {
            GL.Enable(EnableCap.CullFace);
        }
    }

    public void Dispose()
    {
        if (_shader == null && _vertexArray == 0 && _noise == 0 && _exploration.IsDisposed)
        {
            return;
        }

        GLUtils.AssertRenderThread("War mist renderer disposal");
        try
        {
            _shader?.Dispose();
        }
        finally
        {
            _shader = null;
            try
            {
                _exploration.Dispose();
            }
            finally
            {
                if (_noise != 0)
                {
                    GL.DeleteTexture(_noise);
                    _noise = 0;
                }

                if (_vertexArray != 0)
                {
                    GL.DeleteVertexArray(_vertexArray);
                    _vertexArray = 0;
                }
            }
        }
    }

    private static void Upload(int location, float x, float y)
    {
        if (location < 0)
        {
            return;
        }

        GL.Uniform2(location, x, y);
        RenderCounters.CountUniformUpload();
    }

    private static void Upload(int location, float value)
    {
        if (location < 0)
        {
            return;
        }

        GL.Uniform1(location, value);
        RenderCounters.CountUniformUpload();
    }

    private static void Upload(int location, int value)
    {
        if (location < 0)
        {
            return;
        }

        GL.Uniform1(location, value);
        RenderCounters.CountUniformUpload();
    }
}
