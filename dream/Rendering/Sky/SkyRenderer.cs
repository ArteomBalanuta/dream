using Dream.Assets;
using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Sky;

// The sky pass: one fullscreen triangle at depth 1, drawn after the opaque world and actors so it
// only shades the pixels nothing covered. The view ray comes from the CPU-inverted, translation-free
// view-projection; the gradient, sun, moon and stars come from SkyState. One program bind, one draw
// and a fixed set of uniform uploads per frame, through locations cached at construction; nothing
// is allocated per frame.
public sealed class SkyRenderer : IDisposable
{
    public const string VertexShaderFile = "skyVertex.glsl";
    public const string FragmentShaderFile = "skyFragment.glsl";

    // Every uniform the sky shaders declare, in the order of the Location* indices below. A test
    // checks this list against the shader sources, so a new uniform cannot go unset.
    public static readonly string[] UniformNames =
    [
        "uInverseViewProjection",
        "uZenithColor",
        "uHorizonColor",
        "uSun",
        "uSunDiscColor",
        "uMoon",
        "uMoonDiscColor",
        "uStarVisibility",
        "uEyePosition",
    ];

    private const int LocationInverseViewProjection = 0;
    private const int LocationZenithColor = 1;
    private const int LocationHorizonColor = 2;
    private const int LocationSun = 3;
    private const int LocationSunColor = 4;
    private const int LocationMoon = 5;
    private const int LocationMoonColor = 6;
    private const int LocationStarVisibility = 7;
    private const int LocationEyePosition = 8;

    private readonly int[] _locations = new int[UniformNames.Length];
    private Shader? _shader;
    private int _vertexArray;

    public SkyRenderer()
    {
        GLUtils.AssertRenderThread("Sky renderer creation");
        _shader = new Shader(AssetPaths.Shader(VertexShaderFile), AssetPaths.Shader(FragmentShaderFile));
        try
        {
            ReadOnlySpan<int> locations = _shader.GetUniformLocations(UniformNames);
            locations.CopyTo(_locations);
            // Core profile draws need a bound VAO even when no attribute is read.
            _vertexArray = GL.GenVertexArray();
            GLUtils.CheckGLError("Sky renderer creation");
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

    // Draws the sky behind everything already in the depth buffer and restores the world pass
    // state afterwards: depth test on, DepthFunc Less, depth writes on, blending off, and face
    // culling as it was.
    public void Draw(in SkyState sky, Matrix4 view, Matrix4 projection)
    {
        Shader shader = _shader ?? throw new ObjectDisposedException(nameof(SkyRenderer));

        bool cullWasEnabled = GL.IsEnabled(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(false);
        GL.Disable(EnableCap.Blend);
        if (cullWasEnabled)
        {
            GL.Disable(EnableCap.CullFace);
        }

        shader.Use();
        shader.SetMatrix4(_locations[LocationInverseViewProjection], ComputeInverseViewProjection(view, projection));
        Upload(_locations[LocationZenithColor], sky.ZenithColor);
        Upload(_locations[LocationHorizonColor], sky.HorizonColor);
        Upload(_locations[LocationSun], new Vector4(sky.SunDirection, sky.SunDiscAngularRadius));
        Upload(_locations[LocationSunColor], sky.SunDiscColor);
        Upload(_locations[LocationMoon], new Vector4(sky.MoonDirection, sky.MoonDiscAngularRadius));
        Upload(_locations[LocationMoonColor], sky.MoonDiscColor);
        Upload(_locations[LocationStarVisibility], sky.StarVisibility);
        // The real camera position (the view's inverse translation), not the fog origin the lighting block carries.
        Upload(_locations[LocationEyePosition], Matrix4.Invert(view).Row3.Xyz);

        GL.BindVertexArray(_vertexArray);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        RenderCounters.CountDraw();
        GL.BindVertexArray(0);

        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        if (cullWasEnabled)
        {
            GL.Enable(EnableCap.CullFace);
        }
    }

    // inverse(projection-after-rotation) in OpenTK's row-vector layout, ready to upload with
    // transpose = false like every other matrix. The view's translation is dropped first, so the
    // result maps an NDC point to a world-space direction from the eye, whatever the camera position.
    public static Matrix4 ComputeInverseViewProjection(Matrix4 view, Matrix4 projection)
    {
        Matrix4 rotation = view;
        rotation.Row3 = new Vector4(0f, 0f, 0f, 1f);
        return Matrix4.Invert(rotation * projection);
    }

    public void Dispose()
    {
        if (_shader == null && _vertexArray == 0)
        {
            return;
        }

        GLUtils.AssertRenderThread("Sky renderer disposal");
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

    private static void Upload(int location, Vector3 value)
    {
        if (location < 0)
        {
            return;
        }

        GL.Uniform3(location, value);
        RenderCounters.CountUniformUpload();
    }

    private static void Upload(int location, Vector4 value)
    {
        if (location < 0)
        {
            return;
        }

        GL.Uniform4(location, value);
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
}
