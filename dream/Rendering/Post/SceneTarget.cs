using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Post;

/// <summary>
/// The offscreen float buffer the world draws into, and the single-sample copy the post chain
/// reads.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed the whole frame went straight to the default framebuffer, which is fixed
/// point: every channel was clipped at 1.0, and the art had to be kept under an albedo ceiling so
/// that nothing standing beside a brazier landed on a flat white patch. An RGBA16F attachment
/// carries the overbright values instead, which is what lets the shoulder in postTone.glsl give a
/// clipped highlight its shape back and what lets a spark bloom.
/// </para>
/// <para>
/// MSAA is load-bearing in this project - the actor is 28 x 101 pixels at the shipped zoom, so the
/// resolve is most of what makes an edge readable - and an offscreen target does NOT inherit the
/// window's multisampling. So when the context granted samples, the scene draws into a
/// multisampled renderbuffer pair and is blitted down into the texture the chain samples. With no
/// samples the texture is the draw target directly and there is no blit.
/// </para>
/// <para>
/// Every failure path here is non-fatal. If the format or the combination is not renderable,
/// <see cref="IsAvailable"/> stays false, the caller draws to the default framebuffer exactly as
/// before, and the game loses the shoulder and the bloom but nothing else. That is the OpenGL 3.3
/// fallback the brief asks for, and it is also what runs under <c>DREAM_POST=0</c>.
/// </para>
/// </remarks>
public sealed class SceneTarget : IDisposable
{
    private int _multisampleFramebuffer;
    private int _multisampleColor;
    private int _multisampleDepth;
    private int _resolveFramebuffer;
    private int _resolveTexture;
    private int _singleDepth;

    public SceneTarget(int width, int height, int samples)
    {
        GLUtils.AssertRenderThread("Scene target creation");
        Width = Math.Max(width, 1);
        Height = Math.Max(height, 1);
        Samples = Math.Max(samples, 0);
        IsAvailable = TryCreate();
        if (!IsAvailable)
        {
            ReleaseObjects();
        }

        GLUtils.CheckGLError("Scene target creation");
    }

    public int Width { get; }

    public int Height { get; }

    public int Samples { get; }

    /// <summary>False when the buffers could not be made; the caller must then draw to the default framebuffer.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>The single-sample RGBA16F texture the post chain samples. 0 when unavailable.</summary>
    public int ResolvedTexture => _resolveTexture;

    /// <summary>True when the scene is drawn multisampled and <see cref="Resolve"/> has to blit.</summary>
    public bool IsMultisampled => _multisampleFramebuffer != 0;

    public bool Matches(int width, int height, int samples) =>
        Width == Math.Max(width, 1) && Height == Math.Max(height, 1) && Samples == Math.Max(samples, 0);

    /// <summary>Binds the draw target and sets the viewport to it. Call before the frame's clear.</summary>
    public void BindForScene()
    {
        int framebuffer = _multisampleFramebuffer != 0 ? _multisampleFramebuffer : _resolveFramebuffer;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.Viewport(0, 0, Width, Height);
    }

    /// <summary>
    /// Makes <see cref="ResolvedTexture"/> hold the finished scene. A no-op when the scene was drawn
    /// straight into that texture; otherwise a multisample blit, which is the only way to resolve a
    /// multisampled attachment in a 3.3 core profile.
    /// </summary>
    public void Resolve()
    {
        if (_multisampleFramebuffer == 0)
        {
            return;
        }

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _multisampleFramebuffer);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _resolveFramebuffer);
        // Colour only, and Nearest: a multisample blit of a colour buffer must use Nearest, and the
        // depth buffer is not read again after the scene passes.
        GL.BlitFramebuffer(
            0, 0, Width, Height,
            0, 0, Width, Height,
            ClearBufferMask.ColorBufferBit,
            BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private bool TryCreate()
    {
        _resolveTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _resolveTexture);
        GL.TexImage2D(
            TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, Width, Height, 0,
            PixelFormat.Rgba, PixelType.HalfFloat, IntPtr.Zero);
        // Linear so the bright pass can take bilinear taps; clamped so a tap at the edge of the
        // screen does not wrap a bright pixel around to the other side.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        _resolveFramebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _resolveFramebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _resolveTexture, 0);

        if (Samples <= 1)
        {
            // No multisampling: the scene draws straight into the texture, so this framebuffer needs
            // its own depth.
            _singleDepth = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _singleDepth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, Width, Height);
            GL.FramebufferRenderbuffer(
                FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                RenderbufferTarget.Renderbuffer, _singleDepth);
        }

        if (!IsComplete("scene resolve"))
        {
            return false;
        }

        if (Samples > 1 && !TryCreateMultisample())
        {
            return false;
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return true;
    }

    private bool TryCreateMultisample()
    {
        _multisampleColor = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _multisampleColor);
        GL.RenderbufferStorageMultisample(
            RenderbufferTarget.Renderbuffer, Samples, RenderbufferStorage.Rgba16f, Width, Height);

        _multisampleDepth = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _multisampleDepth);
        GL.RenderbufferStorageMultisample(
            RenderbufferTarget.Renderbuffer, Samples, RenderbufferStorage.Depth24Stencil8, Width, Height);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        _multisampleFramebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _multisampleFramebuffer);
        GL.FramebufferRenderbuffer(
            FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, _multisampleColor);
        GL.FramebufferRenderbuffer(
            FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
            RenderbufferTarget.Renderbuffer, _multisampleDepth);
        return IsComplete("scene multisample");
    }

    private static bool IsComplete(string what)
    {
        FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status == FramebufferErrorCode.FramebufferComplete)
        {
            return true;
        }

        // Not a throw: an incomplete framebuffer here costs the shoulder and the bloom, not the game.
        Console.WriteLine($"[post] {what} framebuffer incomplete ({status}); drawing without the post chain");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return false;
    }

    private void ReleaseObjects()
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (_multisampleFramebuffer != 0)
        {
            GL.DeleteFramebuffer(_multisampleFramebuffer);
            _multisampleFramebuffer = 0;
        }

        if (_resolveFramebuffer != 0)
        {
            GL.DeleteFramebuffer(_resolveFramebuffer);
            _resolveFramebuffer = 0;
        }

        if (_multisampleColor != 0)
        {
            GL.DeleteRenderbuffer(_multisampleColor);
            _multisampleColor = 0;
        }

        if (_multisampleDepth != 0)
        {
            GL.DeleteRenderbuffer(_multisampleDepth);
            _multisampleDepth = 0;
        }

        if (_singleDepth != 0)
        {
            GL.DeleteRenderbuffer(_singleDepth);
            _singleDepth = 0;
        }

        if (_resolveTexture != 0)
        {
            GL.DeleteTexture(_resolveTexture);
            _resolveTexture = 0;
        }

        IsAvailable = false;
    }

    public void Dispose()
    {
        if (_resolveTexture == 0 && _resolveFramebuffer == 0 && _multisampleFramebuffer == 0)
        {
            return;
        }

        GLUtils.AssertRenderThread("Scene target disposal");
        ReleaseObjects();
    }
}
