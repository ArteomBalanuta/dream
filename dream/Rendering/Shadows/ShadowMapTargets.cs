using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Shadows;

// The GL objects behind the shadow maps: the sun's depth texture and FBO, the fire atlas texture and FBO, and a 1x1
// dummy depth texture. Nothing is drawn by this class; the caster pass (workstream D2) brackets its draws with
// BeginSun / BeginFireTile and End, and the frame calls BindForSampling after the passes.
//
// Created with enabled == false it allocates ONLY the dummy (no 34 MB of maps), and BindForSampling then binds the
// dummy on both units so the samplers are always valid: a sampler2DShadow bound to nothing is undefined behaviour on
// some drivers, which could read unit 0's colour texture as depth.
public sealed class ShadowMapTargets : IDisposable
{
    private readonly bool _enabled;
    private readonly int[] _savedViewport = new int[4];
    private int _sunTexture;
    private int _sunFramebuffer;
    private int _atlasTexture;
    private int _atlasFramebuffer;
    private int _dummyTexture;

    private bool _active;
    private int _savedFramebuffer;
    private bool _savedDepthTest;
    private bool _savedCullFace;
    private bool _savedBlend;
    private bool _savedDepthMask;
    private int _savedDepthFunc;

    public ShadowMapTargets(bool enabled, int sunResolution, int atlasResolution)
    {
        GLUtils.AssertRenderThread("Shadow map creation");
        _enabled = enabled;
        SunResolution = sunResolution;
        AtlasResolution = atlasResolution;
        TileSide = atlasResolution / ShadowSettings.GridSide;

        _dummyTexture = CreateDummy();
        if (enabled)
        {
            _sunTexture = CreateDepthTexture(PixelInternalFormat.DepthComponent32f, sunResolution);
            _sunFramebuffer = CreateDepthFramebuffer(_sunTexture, "sun");
            _atlasTexture = CreateDepthTexture(PixelInternalFormat.DepthComponent24, atlasResolution);
            _atlasFramebuffer = CreateDepthFramebuffer(_atlasTexture, "fire atlas");
        }

        GLUtils.CheckGLError("Shadow map creation");
    }

    public ShadowMapTargets()
        : this(ShadowSettings.Enabled, ShadowSettings.SunResolution, ShadowSettings.AtlasResolution)
    {
    }

    public int SunResolution { get; }
    public int AtlasResolution { get; }
    public int TileSide { get; }
    public bool Enabled => _enabled;

    // The GL texture names (0 when shadows are disabled).
    public int SunHandle => _sunTexture;
    public int AtlasHandle => _atlasTexture;

    /// <summary>Pixel origin of a (slot, face) tile: slot-major, row-major in the GridSide x GridSide grid.</summary>
    public static (int X, int Y) TileOrigin(int slot, int face, int tileSide)
    {
        if ((uint)slot >= ShadowSettings.MaxCastingFireCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        if ((uint)face >= ShadowSettings.FacesPerLight)
        {
            throw new ArgumentOutOfRangeException(nameof(face));
        }

        int index = slot * ShadowSettings.FacesPerLight + face;
        return (index % ShadowSettings.GridSide * tileSide, index / ShadowSettings.GridSide * tileSide);
    }

    /// <summary>Binds the sun map as the depth target over its whole extent, clears it, and sets the caster state.</summary>
    public void BeginSun()
    {
        RequireEnabled();
        Enter(_sunFramebuffer, 0, 0, SunResolution, SunResolution);
        GL.Clear(ClearBufferMask.DepthBufferBit);
    }

    /// <summary>
    /// Binds the atlas, restricts the viewport to the (slot, face) tile, and clears exactly that tile (scissored, so the
    /// gutter reads 1.0 = lit and a PCF tap at a face edge can never see a neighbour's occluder).
    /// </summary>
    public void BeginFireTile(int slot, int face)
    {
        RequireEnabled();
        (int x, int y) = TileOrigin(slot, face, TileSide);
        Enter(_atlasFramebuffer, x, y, TileSide, TileSide);
        GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(x, y, TileSide, TileSide);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.Disable(EnableCap.ScissorTest);
    }

    /// <summary>Restores the framebuffer, viewport and the fixed-function state that Begin* changed.</summary>
    public void End()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        SetCap(EnableCap.DepthTest, _savedDepthTest);
        SetCap(EnableCap.CullFace, _savedCullFace);
        SetCap(EnableCap.Blend, _savedBlend);
        GL.DepthMask(_savedDepthMask);
        GL.DepthFunc((DepthFunction)_savedDepthFunc);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _savedFramebuffer);
        GL.Viewport(_savedViewport[0], _savedViewport[1], _savedViewport[2], _savedViewport[3]);
    }

    /// <summary>Binds the maps (or the dummy when disabled) to units 5 and 6 and leaves unit 0 active.</summary>
    public void BindForSampling()
    {
        GL.ActiveTexture(TextureUnit.Texture0 + ShadowSettings.SunTextureUnit);
        GL.BindTexture(TextureTarget.Texture2D, _enabled ? _sunTexture : _dummyTexture);
        GL.ActiveTexture(TextureUnit.Texture0 + ShadowSettings.AtlasTextureUnit);
        GL.BindTexture(TextureTarget.Texture2D, _enabled ? _atlasTexture : _dummyTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose()
    {
        if (_sunTexture == 0 && _atlasTexture == 0 && _dummyTexture == 0 && _sunFramebuffer == 0 && _atlasFramebuffer == 0)
        {
            return;
        }

        GLUtils.AssertRenderThread("Shadow map disposal");
        Delete(ref _sunFramebuffer, isFramebuffer: true);
        Delete(ref _atlasFramebuffer, isFramebuffer: true);
        Delete(ref _sunTexture, isFramebuffer: false);
        Delete(ref _atlasTexture, isFramebuffer: false);
        Delete(ref _dummyTexture, isFramebuffer: false);
    }

    private void Enter(int framebuffer, int x, int y, int width, int height)
    {
        if (_active)
        {
            // Tile to tile within one pass: the saved state is the pre-pass state, keep it.
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.Viewport(x, y, width, height);
            return;
        }

        _active = true;
        GL.GetInteger(GetPName.FramebufferBinding, out _savedFramebuffer);
        GL.GetInteger(GetPName.Viewport, _savedViewport);
        _savedDepthTest = GL.IsEnabled(EnableCap.DepthTest);
        _savedCullFace = GL.IsEnabled(EnableCap.CullFace);
        _savedBlend = GL.IsEnabled(EnableCap.Blend);
        GL.GetBoolean(GetPName.DepthWritemask, out _savedDepthMask);
        GL.GetInteger(GetPName.DepthFunc, out _savedDepthFunc);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.Viewport(x, y, width, height);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        GL.Disable(EnableCap.Blend);
        // Culling stays OFF in the caster pass: chunk meshes are open shells of exposed faces, so culling front faces
        // would delete the ground's top faces from the map, and the flora is double sided. Acne is beaten by bias alone.
        GL.Disable(EnableCap.CullFace);
    }

    private void RequireEnabled()
    {
        if (!_enabled)
        {
            throw new InvalidOperationException("Shadow maps are disabled (DREAM_SHADOWS=0); there is nothing to render into.");
        }
    }

    private static void SetCap(EnableCap cap, bool on)
    {
        if (on)
        {
            GL.Enable(cap);
        }
        else
        {
            GL.Disable(cap);
        }
    }

    private static int CreateDepthTexture(PixelInternalFormat format, int side)
    {
        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, format, side, side, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        ConfigureShadowSampling();
        // Outside the volume reads 1.0, which LEQUAL against any reference <= 1 passes: fully lit.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, [1f, 1f, 1f, 1f]);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    // COMPARE_REF_TO_TEXTURE + LEQUAL + LINEAR: each tap is a hardware 2x2 percentage-closer comparison.
    private static void ConfigureShadowSampling()
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)All.Lequal);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
    }

    private static int CreateDummy()
    {
        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        float[] one = [1f];
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent16, 1, 1, 0, PixelFormat.DepthComponent, PixelType.Float, one);
        ConfigureShadowSampling();
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private static int CreateDepthFramebuffer(int depthTexture, string what)
    {
        int framebuffer = GL.GenFramebuffer();
        GL.GetInteger(GetPName.FramebufferBinding, out int previous);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depthTexture, 0);
        // Depth only: no colour attachment to draw or read.
        SelectNoDrawBuffer();
        GL.ReadBuffer(ReadBufferMode.None);
        FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, previous);
        if (status != FramebufferErrorCode.FramebufferComplete)
        {
            GL.DeleteFramebuffer(framebuffer);
            throw new InvalidOperationException($"The {what} shadow framebuffer is incomplete: {status}.");
        }

        return framebuffer;
    }

    // Selecting "no colour output" is framebuffer state, not a draw; RenderCountersTests treats any statement that starts
    // with GL.Draw as a draw call needing a CountDraw, so the call lives behind this name.
    private static void SelectNoDrawBuffer() => GL.DrawBuffer(DrawBufferMode.None);

    private static void Delete(ref int handle, bool isFramebuffer)
    {
        if (handle == 0)
        {
            return;
        }

        if (isFramebuffer)
        {
            GL.DeleteFramebuffer(handle);
        }
        else
        {
            GL.DeleteTexture(handle);
        }

        handle = 0;
    }
}
