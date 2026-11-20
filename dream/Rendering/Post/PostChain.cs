using Dream.Assets;
using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Post;

/// <summary>
/// The scene post chain: bright pass, separable blur, resolve. Three programs, four small draws
/// and no allocation per frame.
/// </summary>
/// <remarks>
/// <para>
/// What it does, in order: take the part of the HDR scene that sits above the tone knee (and
/// nothing else - below the knee the bright pass returns exactly zero, so a dark frame produces a
/// black bloom and the glow cannot lift a shadow), blur it at a quarter resolution across and then
/// down, add it back on top of the scene, and bend the sum over the shoulder on the way to the
/// default framebuffer.
/// </para>
/// <para>
/// The shoulder is the identity below the knee, so the overwhelming majority of every frame comes
/// through this pass unchanged. What changes is the top end: before it, anything over 1.0 was a
/// flat white patch - that is what the whole 0.42 albedo ceiling exists to avoid - and now it keeps
/// its shape and its colour.
/// </para>
/// </remarks>
public sealed class PostChain : IDisposable
{
    public const string VertexShaderFile = "postVertex.glsl";
    public const string BrightShaderFile = "postBrightFragment.glsl";
    public const string BlurShaderFile = "postBlurFragment.glsl";
    public const string ResolveShaderFile = "postResolveFragment.glsl";

    // Every uniform the three fragment shaders declare. PostChainUniformContractTests checks these
    // against the sources in both directions, so a uniform cannot be added to a shader and left
    // unset here, and a misspelt name cannot silently resolve to location -1.
    public static readonly string[] BrightUniformNames = ["uScene", "uSourceTexel"];
    public static readonly string[] BlurUniformNames = ["uSource", "uDirection"];
    public static readonly string[] ResolveUniformNames = ["uScene", "uBloom", "uBloomStrength", "uShadowNeutralise"];

    private readonly float _bloomStrength;
    private readonly float _shadowNeutralise;
    private Shader? _bright;
    private Shader? _blur;
    private Shader? _resolve;
    private int _vertexArray;

    private readonly int[] _brightLocations = new int[BrightUniformNames.Length];
    private readonly int[] _blurLocations = new int[BlurUniformNames.Length];
    private readonly int[] _resolveLocations = new int[ResolveUniformNames.Length];

    private readonly int[] _bloomFramebuffers = new int[2];
    private readonly int[] _bloomTextures = new int[2];
    private int _bloomWidth;
    private int _bloomHeight;

    public PostChain(float bloomStrength, float shadowNeutraliseStrength)
    {
        GLUtils.AssertRenderThread("Post chain creation");
        _bloomStrength = Math.Clamp(bloomStrength, 0f, 2f);
        _shadowNeutralise = Math.Clamp(shadowNeutraliseStrength, 0f, 1f);
        _bright = new Shader(AssetPaths.Shader(VertexShaderFile), AssetPaths.Shader(BrightShaderFile));
        try
        {
            _blur = new Shader(AssetPaths.Shader(VertexShaderFile), AssetPaths.Shader(BlurShaderFile));
            _resolve = new Shader(AssetPaths.Shader(VertexShaderFile), AssetPaths.Shader(ResolveShaderFile));
            _bright.GetUniformLocations(BrightUniformNames).CopyTo(_brightLocations);
            _blur.GetUniformLocations(BlurUniformNames).CopyTo(_blurLocations);
            _resolve.GetUniformLocations(ResolveUniformNames).CopyTo(_resolveLocations);
            // Core profile draws need a bound VAO even when no attribute is read.
            _vertexArray = GL.GenVertexArray();
            GLUtils.CheckGLError("Post chain creation");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsDisposed => _resolve == null;

    // The three program handles, for the shader warm-up's bookkeeping.
    public IEnumerable<int> ProgramHandles => new[] { _bright?.Handle ?? 0, _blur?.Handle ?? 0, _resolve?.Handle ?? 0 };

    public float BloomStrength => _bloomStrength;

    public float ShadowNeutraliseStrength => _shadowNeutralise;

    /// <summary>The quarter-resolution bloom side for a scene side, never under the blur's reach.</summary>
    public static int BloomSide(int sceneSide) =>
        Math.Max(sceneSide / PostSettings.BloomDownscale, PostSettings.MinimumBloomSide);

    /// <summary>
    /// Runs the chain and leaves the output framebuffer (the default one unless the shader warm-up gave its own) bound with the scene on it, the viewport
    /// covering the window and the state the HUD expects (blending off, depth test off, depth
    /// writes on).
    /// </summary>
    public void Run(int sceneTexture, int width, int height, int outputFramebuffer = 0)
    {
        Shader bright = _bright ?? throw new ObjectDisposedException(nameof(PostChain));
        Shader blur = _blur!;
        Shader resolve = _resolve!;

        EnsureBloomTargets(width, height);

        // The whole chain is unlit, untested, unblended full-screen work. Set that once rather than
        // per draw, and put back what the HUD assumes at the end.
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.DepthMask(false);
        GL.BindVertexArray(_vertexArray);

        // 1. Bright pass: scene -> bloom[0], at a quarter in each axis.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _bloomFramebuffers[0]);
        GL.Viewport(0, 0, _bloomWidth, _bloomHeight);
        bright.Use();
        BindTexture(TextureUnit.Texture0, sceneTexture);
        bright.SetInt(_brightLocations[0], 0);
        bright.SetVector2(_brightLocations[1], new OpenTK.Mathematics.Vector2(1f / Math.Max(width, 1), 1f / Math.Max(height, 1)));
        DrawFullscreen();

        // 2. Blur across, then down, through the ping-pong pair.
        blur.Use();
        blur.SetInt(_blurLocations[0], 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _bloomFramebuffers[1]);
        BindTexture(TextureUnit.Texture0, _bloomTextures[0]);
        blur.SetVector2(_blurLocations[1], new OpenTK.Mathematics.Vector2(1f / _bloomWidth, 0f));
        DrawFullscreen();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _bloomFramebuffers[0]);
        BindTexture(TextureUnit.Texture0, _bloomTextures[1]);
        blur.SetVector2(_blurLocations[1], new OpenTK.Mathematics.Vector2(0f, 1f / _bloomHeight));
        DrawFullscreen();

        // 3. Resolve to the window (the shader warm-up passes its own small target instead).
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, outputFramebuffer);
        GL.Viewport(0, 0, width, height);
        resolve.Use();
        BindTexture(TextureUnit.Texture0, sceneTexture);
        BindTexture(TextureUnit.Texture1, _bloomTextures[0]);
        resolve.SetInt(_resolveLocations[0], 0);
        resolve.SetInt(_resolveLocations[1], 1);
        resolve.SetFloat(_resolveLocations[2], _bloomStrength);
        resolve.SetFloat(_resolveLocations[3], _shadowNeutralise);
        DrawFullscreen();

        GL.BindVertexArray(0);
        // Leave unit 0 current, as every other pass in this renderer does.
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.DepthMask(true);
    }

    private static void BindTexture(TextureUnit unit, int texture)
    {
        GL.ActiveTexture(unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
    }

    private static void DrawFullscreen()
    {
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        RenderCounters.CountDraw();
    }

    private void EnsureBloomTargets(int width, int height)
    {
        int bloomWidth = BloomSide(width);
        int bloomHeight = BloomSide(height);
        if (bloomWidth == _bloomWidth && bloomHeight == _bloomHeight && _bloomFramebuffers[0] != 0)
        {
            return;
        }

        ReleaseBloomTargets();
        _bloomWidth = bloomWidth;
        _bloomHeight = bloomHeight;
        for (int i = 0; i < 2; i++)
        {
            _bloomTextures[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _bloomTextures[i]);
            GL.TexImage2D(
                TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, bloomWidth, bloomHeight, 0,
                PixelFormat.Rgba, PixelType.HalfFloat, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            // Clamped: a blur tap past the edge must not wrap a torch from the other side of the screen in.
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            _bloomFramebuffers[i] = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _bloomFramebuffers[i]);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _bloomTextures[i], 0);
            FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                Console.WriteLine($"[post] bloom framebuffer {i} incomplete ({status})");
            }
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GLUtils.CheckGLError("Post chain bloom targets");
    }

    private void ReleaseBloomTargets()
    {
        for (int i = 0; i < 2; i++)
        {
            if (_bloomFramebuffers[i] != 0)
            {
                GL.DeleteFramebuffer(_bloomFramebuffers[i]);
                _bloomFramebuffers[i] = 0;
            }

            if (_bloomTextures[i] != 0)
            {
                GL.DeleteTexture(_bloomTextures[i]);
                _bloomTextures[i] = 0;
            }
        }

        _bloomWidth = 0;
        _bloomHeight = 0;
    }

    public void Dispose()
    {
        if (_bright == null && _blur == null && _resolve == null && _vertexArray == 0)
        {
            return;
        }

        GLUtils.AssertRenderThread("Post chain disposal");
        ReleaseBloomTargets();
        if (_vertexArray != 0)
        {
            GL.DeleteVertexArray(_vertexArray);
            _vertexArray = 0;
        }

        _bright?.Dispose();
        _blur?.Dispose();
        _resolve?.Dispose();
        _bright = null;
        _blur = null;
        _resolve = null;
    }
}
