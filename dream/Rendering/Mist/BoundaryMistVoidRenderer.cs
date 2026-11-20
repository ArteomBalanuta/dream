using Dream.Assets;
using Dream.Rendering.Core;
using Dream.Rendering.Sky;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Mist;

// The boundary mist's void pass: one fullscreen triangle at depth 1 (skyVertex.glsl), drawn after
// the opaque world, the actors and the sky, that paints smoke on every pixel whose view ray
// reaches the floor plane outside the map. Pixels a surface covered keep their own per-fragment
// mist (boundaryMist.glsl). One program bind, one draw and one uniform upload per frame through
// locations cached at construction; nothing is allocated per frame.
public sealed class BoundaryMistVoidRenderer : IDisposable
{
    public const string VertexShaderFile = SkyRenderer.VertexShaderFile;
    public const string FragmentShaderFile = "boundaryVoidFragment.glsl";

    // Every uniform the void shaders declare, in the order of the Location* indices below. A test
    // checks this list against the shader sources, so a new uniform cannot go unset.
    public static readonly string[] UniformNames =
    [
        "uInverseViewProjection",
    ];

    private const int LocationInverseViewProjection = 0;

    private readonly int[] _locations = new int[UniformNames.Length];
    private Shader? _shader;
    private int _vertexArray;

    public BoundaryMistVoidRenderer()
    {
        GLUtils.AssertRenderThread("Boundary mist void renderer creation");
        _shader = new Shader(AssetPaths.Shader(VertexShaderFile), AssetPaths.Shader(FragmentShaderFile));
        try
        {
            ReadOnlySpan<int> locations = _shader.GetUniformLocations(UniformNames);
            locations.CopyTo(_locations);
            // Core profile draws need a bound VAO even when no attribute is read.
            _vertexArray = GL.GenVertexArray();
            GLUtils.CheckGLError("Boundary mist void renderer creation");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsDisposed => _shader == null;

    // The GL program handle, for the shader warm-up's bookkeeping.
    public int ProgramHandle => _shader?.Handle ?? 0;

    // Draws the smoke behind everything already in the depth buffer (depth 1, DepthFunc Lequal, no
    // depth writes, premultiplied blend) and restores the world pass state afterwards: DepthFunc
    // Less, depth writes on, blending off, and face culling as it was. The true camera position
    // (which FrameLighting does not carry in isometric view) comes from the BoundaryMist block.
    public void Draw(Matrix4 view, Matrix4 projection)
    {
        Shader shader = _shader ?? throw new ObjectDisposedException(nameof(BoundaryMistVoidRenderer));

        bool cullWasEnabled = GL.IsEnabled(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(false);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        if (cullWasEnabled)
        {
            GL.Disable(EnableCap.CullFace);
        }

        shader.Use();
        shader.SetMatrix4(_locations[LocationInverseViewProjection], SkyRenderer.ComputeInverseViewProjection(view, projection));
        GL.BindVertexArray(_vertexArray);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        RenderCounters.CountDraw();
        GL.BindVertexArray(0);

        GL.Disable(EnableCap.Blend);
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        if (cullWasEnabled)
        {
            GL.Enable(EnableCap.CullFace);
        }
    }

    public void Dispose()
    {
        if (_shader == null && _vertexArray == 0)
        {
            return;
        }

        GLUtils.AssertRenderThread("Boundary mist void renderer disposal");
        try
        {
            _shader?.Dispose();
        }
        finally
        {
            _shader = null;
            if (_vertexArray != 0)
            {
                GL.DeleteVertexArray(_vertexArray);
                _vertexArray = 0;
            }
        }
    }
}
