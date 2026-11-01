using System.Runtime.CompilerServices;
using Dream.Assets;
using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Ui.Gl;

// The only part of the classic HUD that talks to OpenGL for drawing. Owns one program, one VAO, a
// dynamic vertex buffer sized for a full batch and a static index buffer. Submit uploads the used
// part of the vertex array with one BufferSubData, draws one call per UiDrawCommand and restores the
// GL state it changed. Render thread only.
public sealed class UiGlRenderer : IDisposable
{
    private static readonly int VertexSize = Unsafe.SizeOf<UiVertex>();

    private readonly Shader _shader;
    private readonly int _viewportLocation;
    private readonly int _textureLocation;
    private int _vao;
    private int _vbo;
    private int _ebo;
    private bool _disposed;
    private bool _firstDrawChecked;

    public UiGlRenderer()
    {
        GLUtils.AssertRenderThread("UiGlRenderer creation");
        _shader = new Shader(AssetPaths.Shader("uiVertex.glsl"), AssetPaths.Shader("uiFragment.glsl"));
        try
        {
            _viewportLocation = GL.GetUniformLocation(_shader.Handle, "uViewport");
            _textureLocation = GL.GetUniformLocation(_shader.Handle, "uTexture");

            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            _ebo = GL.GenBuffer();

            ushort[] indices = UiBatch.CreateQuadIndices();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, UiBatch.MaxQuads * 4 * VertexSize, IntPtr.Zero, BufferUsageHint.StreamDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(ushort), indices, BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, VertexSize, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, VertexSize, 8);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, VertexSize, 16);
            GL.EnableVertexAttribArray(3);
            GL.VertexAttribPointer(3, 4, VertexAttribPointerType.UnsignedByte, true, VertexSize, 20);
            GL.EnableVertexAttribArray(4);
            GL.VertexAttribIPointer(4, 1, VertexAttribIntegerType.UnsignedInt, VertexSize, 24);
            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            if (GLUtils.CheckGLError("UiGlRenderer creation") != GLFaultKind.None)
            {
                throw new InvalidOperationException("OpenGL reported an error while creating the classic HUD renderer.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Draws the batch. fontTexture is the ImGui font texture (UiPage.Font); the other pages come
    // from the atlas store. Commands whose texture is missing are skipped. State is restored even
    // if a GL call throws.
    public void Submit(UiBatch batch, UiAtlasStore atlas, int fontTexture)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(atlas);
        GLUtils.AssertRenderThread("UiGlRenderer.Submit");
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(UiGlRenderer));
        }

        if (batch.QuadCount == 0)
        {
            return;
        }

        // Checked once, on the first frame that draws, and then latched: the first draw is where a
        // broken shader or buffer shows, and one glGetError per frame is not free. Errors already
        // pending from other code are drained first so they are not blamed on this renderer.
        bool checkThisFrame = !_firstDrawChecked;
        if (checkThisFrame)
        {
            for (int n = 0; n < GLUtils.MaxErrorsDrainedPerCheck && GL.GetError() != ErrorCode.NoError; n++)
            {
            }
        }

        bool drew = false;
        ImGuiGlStateSnapshot previous = ImGuiGlStateSnapshot.Capture();
        try
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFuncSeparate(
                BlendingFactorSrc.One,
                BlendingFactorDest.OneMinusSrcAlpha,
                BlendingFactorSrc.One,
                BlendingFactorDest.OneMinusSrcAlpha);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.ScissorTest);
            GL.ActiveTexture(TextureUnit.Texture0);

            _shader.Use();
            GL.Uniform2(_viewportLocation, (float)batch.ViewportWidth, (float)batch.ViewportHeight);
            GL.Uniform1(_textureLocation, 0);

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            ReadOnlySpan<UiVertex> vertices = batch.Vertices;
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertices.Length * VertexSize, ref Unsafe.AsRef(in vertices[0]));

            int boundTexture = -1;
            foreach (ref readonly UiDrawCommand cmd in batch.Commands)
            {
                int texture = cmd.Page == UiPage.Font ? fontTexture : atlas.GetTexture(cmd.Page);
                UiClipRect clip = cmd.Clip;
                if (texture == 0 || clip.IsEmpty)
                {
                    continue;
                }

                if (texture != boundTexture)
                {
                    GL.BindTexture(TextureTarget.Texture2D, texture);
                    boundTexture = texture;
                }

                // Scissor is bottom-left origin; the batch is top-left.
                GL.Scissor(clip.X0, batch.ViewportHeight - clip.Y1, clip.X1 - clip.X0, clip.Y1 - clip.Y0);
                GL.DrawElements(PrimitiveType.Triangles, cmd.IndexCount, DrawElementsType.UnsignedShort, cmd.FirstIndex * sizeof(ushort));
                RenderCounters.CountDraw();
                drew = true;
            }

            if (checkThisFrame && drew)
            {
                _firstDrawChecked = true;
                if (GLUtils.CheckGLError("UiGlRenderer first draw") != GLFaultKind.None)
                {
                    throw new InvalidOperationException("OpenGL reported an error on the first classic HUD draw.");
                }
            }
        }
        finally
        {
            previous.Restore();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GLUtils.AssertRenderThread("UiGlRenderer disposal");
        _shader.Dispose();
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
        GL.DeleteVertexArray(_vao);
        _vbo = 0;
        _ebo = 0;
        _vao = 0;
    }
}
