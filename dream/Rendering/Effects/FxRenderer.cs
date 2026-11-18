using Dream.Assets;
using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Effects;

// The effects pass: every billboard (particles and halos) in one instanced draw of a four-vertex
// strip, premultiplied-alpha blended over the back buffer. The corners come from gl_VertexID, so
// there is no mesh VBO; the per-instance data is the FxInstance array, uploaded through
// FxInstanceBuffer. Depth is tested against the finished world but not written, and the fragment
// shader fades with the same fog the world uses, so the pass needs no sorting and no offscreen
// target. One program bind, one upload, one draw and a fixed set of uniform uploads per frame,
// through locations cached at construction; nothing is allocated per frame.
public sealed class FxRenderer : IDisposable
{
    public const string VertexShaderFile = "fxVertex.glsl";
    public const string FragmentShaderFile = "fxFragment.glsl";

    // Every uniform the effects shaders declare, in the order of the Location* indices below. A
    // test checks this list against the shader sources, so a new uniform cannot go unset.
    public static readonly string[] UniformNames =
    [
        "uViewProjection",
        "uCameraRight",
        "uCameraUp",
        "uCameraBack",
        "uAtlas",
    ];

    private const int LocationViewProjection = 0;
    private const int LocationCameraRight = 1;
    private const int LocationCameraUp = 2;
    private const int LocationCameraBack = 3;
    private const int LocationAtlas = 4;

    private readonly int[] _locations = new int[UniformNames.Length];
    private readonly FxInstanceBuffer _instances = new();
    private Shader? _shader;
    private int _vertexArray;
    private int _atlas;

    public FxRenderer()
    {
        GLUtils.AssertRenderThread("Effects renderer creation");
        try
        {
            _shader = new Shader(AssetPaths.Shader(VertexShaderFile), AssetPaths.Shader(FragmentShaderFile));
            ReadOnlySpan<int> locations = _shader.GetUniformLocations(UniformNames);
            locations.CopyTo(_locations);

            // Rows run downward and the atlas is read with no flip (the vertex shader flips V).
            DecodedTexture atlas = TextureDecoder.DecodeRgba(AssetPaths.Ui(FxAtlas.RelativePath));
            int previous = GL.GetInteger(GetPName.TextureBinding2D);
            try
            {
                _atlas = TextureDecoder.UploadTexture2DClamped(atlas, FxAtlas.MaxMipLevel);
            }
            finally
            {
                GL.BindTexture(TextureTarget.Texture2D, previous);
            }

            _vertexArray = GL.GenVertexArray();
            GL.BindVertexArray(_vertexArray);
            _instances.BindAttributes();
            GL.BindVertexArray(0);
            // GL_ARRAY_BUFFER is not part of VAO state, so a binding left by the buffer has to be
            // cleared separately: otherwise the next buffer write that forgets to bind lands in our
            // instances. FxInstanceBuffer clears it in BindAttributes and in every per-frame Upload
            // - this belt-and-braces line keeps creation correct whatever it does.
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GLUtils.CheckGLError("Effects renderer creation");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsDisposed => _shader == null;

    // Draws the first `count` instances over the world and restores the world pass state
    // afterwards: depth writes on, blending off, face culling as it was, no VAO.
    public void Draw(FxInstance[] instances, int count, in FxViewBasis basis, Matrix4 viewProjection)
    {
        Shader shader = _shader ?? throw new ObjectDisposedException(nameof(FxRenderer));
        if (count <= 0)
        {
            return;
        }

        _instances.Upload(instances, count);

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
        Upload(_locations[LocationCameraRight], basis.Right);
        Upload(_locations[LocationCameraUp], basis.Up);
        Upload(_locations[LocationCameraBack], basis.Back);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _atlas);
        Upload(_locations[LocationAtlas], 0);

        GL.BindVertexArray(_vertexArray);
        GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, count);
        RenderCounters.CountDraw();
        GL.BindVertexArray(0);

        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        if (cullWasEnabled)
        {
            GL.Enable(EnableCap.CullFace);
        }
    }

    public void Dispose()
    {
        if (_shader == null && _vertexArray == 0 && _atlas == 0 && _instances.Handle == 0)
        {
            return;
        }

        GLUtils.AssertRenderThread("Effects renderer disposal");
        try
        {
            _shader?.Dispose();
        }
        finally
        {
            _shader = null;
            if (_atlas != 0)
            {
                GL.DeleteTexture(_atlas);
                _atlas = 0;
            }

            if (_vertexArray != 0)
            {
                GL.DeleteVertexArray(_vertexArray);
                _vertexArray = 0;
            }

            _instances.Dispose();
        }
    }

    private static void Upload(int location, Vector3 value)
    {
        if (location < 0)
        {
            return;
        }

        GL.Uniform3(location, value);
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
