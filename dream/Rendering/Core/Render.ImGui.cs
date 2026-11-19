using Dream.Assets;
using Dream.Rendering.Ui;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using ImGuiNET;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Dream.Rendering.Core;

// A glScissor rectangle in framebuffer pixels with a bottom-left origin.
public readonly record struct ImGuiScissorRect(int X, int Y, int Width, int Height)
{
    // ImGui clip rects are (minX, minY, maxX, maxY) in display space with a top-left origin.
    // They are offset by the draw data's display position, scaled to framebuffer pixels and
    // flipped vertically. An empty or inverted rect has nothing to draw and returns false.
    public static bool TryFromClipRect(
        System.Numerics.Vector4 clipRect,
        System.Numerics.Vector2 displayPos,
        System.Numerics.Vector2 framebufferScale,
        int framebufferHeight,
        out ImGuiScissorRect scissor)
    {
        float minX = (clipRect.X - displayPos.X) * framebufferScale.X;
        float minY = (clipRect.Y - displayPos.Y) * framebufferScale.Y;
        float maxX = (clipRect.Z - displayPos.X) * framebufferScale.X;
        float maxY = (clipRect.W - displayPos.Y) * framebufferScale.Y;
        if (maxX <= minX || maxY <= minY)
        {
            scissor = default;
            return false;
        }

        scissor = new ImGuiScissorRect(
            (int)minX,
            (int)(framebufferHeight - maxY),
            (int)(maxX - minX),
            (int)(maxY - minY));
        return true;
    }
}

// The GL state an ImGui pass changes, captured before the pass and restored after it, so the
// HUD does not leak blend, cull, depth, scissor (flag and box), texture-unit, program,
// vertex-array or array-buffer state into the next frame's world passes.
internal readonly struct ImGuiGlStateSnapshot
{
    private readonly bool _blend;
    private readonly bool _cullFace;
    private readonly bool _depthTest;
    private readonly bool _scissorTest;
    private readonly int _blendSrcRgb;
    private readonly int _blendDstRgb;
    private readonly int _blendSrcAlpha;
    private readonly int _blendDstAlpha;
    private readonly int _activeTexture;
    private readonly int _texture2DOnUnit0;
    private readonly int _program;
    private readonly int _vertexArray;
    private readonly int _arrayBuffer;
    private readonly int _scissorX;
    private readonly int _scissorY;
    private readonly int _scissorWidth;
    private readonly int _scissorHeight;

    private ImGuiGlStateSnapshot(
        bool blend,
        bool cullFace,
        bool depthTest,
        bool scissorTest,
        int blendSrcRgb,
        int blendDstRgb,
        int blendSrcAlpha,
        int blendDstAlpha,
        int activeTexture,
        int texture2DOnUnit0,
        int program,
        int vertexArray,
        int arrayBuffer,
        int scissorX,
        int scissorY,
        int scissorWidth,
        int scissorHeight)
    {
        _blend = blend;
        _cullFace = cullFace;
        _depthTest = depthTest;
        _scissorTest = scissorTest;
        _blendSrcRgb = blendSrcRgb;
        _blendDstRgb = blendDstRgb;
        _blendSrcAlpha = blendSrcAlpha;
        _blendDstAlpha = blendDstAlpha;
        _activeTexture = activeTexture;
        _texture2DOnUnit0 = texture2DOnUnit0;
        _program = program;
        _vertexArray = vertexArray;
        _arrayBuffer = arrayBuffer;
        _scissorX = scissorX;
        _scissorY = scissorY;
        _scissorWidth = scissorWidth;
        _scissorHeight = scissorHeight;
    }

    // Leaves texture unit 0 active, which is the unit the ImGui pass samples from.
    public static ImGuiGlStateSnapshot Capture()
    {
        int activeTexture = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        // GL_SCISSOR_BOX is four ints; it is read into the stack so a capture allocates nothing.
        Span<int> scissorBox = stackalloc int[4];
        unsafe
        {
            fixed (int* box = scissorBox)
            {
                GL.GetInteger(GetPName.ScissorBox, box);
            }
        }

        return new ImGuiGlStateSnapshot(
            GL.IsEnabled(EnableCap.Blend),
            GL.IsEnabled(EnableCap.CullFace),
            GL.IsEnabled(EnableCap.DepthTest),
            GL.IsEnabled(EnableCap.ScissorTest),
            GL.GetInteger(GetPName.BlendSrcRgb),
            GL.GetInteger(GetPName.BlendDstRgb),
            GL.GetInteger(GetPName.BlendSrcAlpha),
            GL.GetInteger(GetPName.BlendDstAlpha),
            activeTexture,
            GL.GetInteger(GetPName.TextureBinding2D),
            GL.GetInteger(GetPName.CurrentProgram),
            GL.GetInteger(GetPName.VertexArrayBinding),
            GL.GetInteger(GetPName.ArrayBufferBinding),
            scissorBox[0],
            scissorBox[1],
            scissorBox[2],
            scissorBox[3]);
    }

    public void Restore()
    {
        SetEnabled(EnableCap.Blend, _blend);
        SetEnabled(EnableCap.CullFace, _cullFace);
        SetEnabled(EnableCap.DepthTest, _depthTest);
        SetEnabled(EnableCap.ScissorTest, _scissorTest);
        GL.Scissor(_scissorX, _scissorY, _scissorWidth, _scissorHeight);
        GL.BlendFuncSeparate(
            (BlendingFactorSrc)_blendSrcRgb,
            (BlendingFactorDest)_blendDstRgb,
            (BlendingFactorSrc)_blendSrcAlpha,
            (BlendingFactorDest)_blendDstAlpha);
        GL.UseProgram(_program);
        GL.BindVertexArray(_vertexArray);
        // GL_ARRAY_BUFFER is not part of VAO state, so it is restored separately.
        GL.BindBuffer(BufferTarget.ArrayBuffer, _arrayBuffer);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _texture2DOnUnit0);
        GL.ActiveTexture((TextureUnit)_activeTexture);
    }

    private static void SetEnabled(EnableCap capability, bool enabled)
    {
        if (enabled)
        {
            GL.Enable(capability);
        }
        else
        {
            GL.Disable(capability);
        }
    }
}

public partial class Render
{
    [MemberNotNull(nameof(_shaderObj))]
    private void InitializeImGui()
    {
        _imGuiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imGuiContext);

        var io = ImGui.GetIO();
        UiFontSetup.AddFonts(io);
        Vector2i safeViewportSize = GetSafeViewportSize(_currentSize);
        io.DisplaySize = new System.Numerics.Vector2(safeViewportSize.X, safeViewportSize.Y);

        io.ConfigWindowsMoveFromTitleBarOnly = true;
        io.ConfigWindowsResizeFromEdges = false;

        CreateFontTexture();
        InitializeUiGlyphs();

        CreateImGuiResources();
    }

    private void CreateFontTexture()
    {
        ImGui.GetIO().Fonts.GetTexDataAsRGBA32(out IntPtr pixels,
            out int width, out int height, out _);

        _fontTexture = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        // GetTexDataAsRGBA32 returns R,G,B,A bytes.
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);

        ImGui.GetIO().Fonts.SetTexID((IntPtr)_fontTexture);
    }

    // Uniform locations, the sampler unit and the vertex layout are fixed, so they are set up
    // once here instead of on every frame and every command list.
    [MemberNotNull(nameof(_shaderObj))]
    private void CreateImGuiResources()
    {
        _shaderObj = new Shader(
            AssetPaths.Shader("hudVertexText.glsl"),
            AssetPaths.Shader("hudFragmentText.glsl"));
        _shader = _shaderObj.Handle;
        _projMtxLocation = GL.GetUniformLocation(_shader, "ProjMtx");
        int textureLocation = GL.GetUniformLocation(_shader, "Texture");
        GL.UseProgram(_shader);
        GL.Uniform1(textureLocation, 0);
        GL.UseProgram(0);

        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        _indexBuffer = GL.GenBuffer();

        int stride = Unsafe.SizeOf<ImDrawVert>();
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    // Draws ImGui with its own blend, cull, depth and scissor state on texture unit 0, then
    // restores the state it found so the world passes are not affected.
    private void RenderImGuiDrawData(ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount == 0) return;

        ImGuiGlStateSnapshot previousState = ImGuiGlStateSnapshot.Capture();
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ScissorTest);
        GL.ActiveTexture(TextureUnit.Texture0);

        System.Numerics.Vector2 displayPos = drawData.DisplayPos;
        System.Numerics.Vector2 displaySize = drawData.DisplaySize;
        System.Numerics.Vector2 framebufferScale = drawData.FramebufferScale;
        int framebufferHeight = (int)(displaySize.Y * framebufferScale.Y);
        var mvp = Matrix4.CreateOrthographicOffCenter(
            displayPos.X,
            displayPos.X + displaySize.X,
            displayPos.Y + displaySize.Y,
            displayPos.Y,
            -1.0f, 1.0f);

        GL.UseProgram(_shader);
        GL.UniformMatrix4(_projMtxLocation, false, ref mvp);

        GL.BindVertexArray(_vertexArray);

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer,
                cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>(),
                cmdList.VtxBuffer.Data,
                BufferUsageHint.StreamDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferData(BufferTarget.ElementArrayBuffer,
                cmdList.IdxBuffer.Size * sizeof(ushort),
                cmdList.IdxBuffer.Data,
                BufferUsageHint.StreamDraw);

            for (int cmd_i = 0; cmd_i < cmdList.CmdBuffer.Size; cmd_i++)
            {
                var pcmd = cmdList.CmdBuffer[cmd_i];
                if (pcmd.UserCallback != IntPtr.Zero) continue;
                if (!ImGuiScissorRect.TryFromClipRect(
                    pcmd.ClipRect,
                    displayPos,
                    framebufferScale,
                    framebufferHeight,
                    out ImGuiScissorRect scissor))
                {
                    continue;
                }

                GL.BindTexture(TextureTarget.Texture2D, (int)pcmd.TextureId);
                GL.Scissor(scissor.X, scissor.Y, scissor.Width, scissor.Height);

                GL.DrawElements(PrimitiveType.Triangles,
                    (int)pcmd.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (int)pcmd.IdxOffset * sizeof(ushort));
                RenderCounters.CountDraw();
            }
        }

        previousState.Restore();
    }
}
