using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Dream.Assets;
using Dream.Client.Input;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Core;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Client.Startup;


public sealed class StartupMenuRenderer : IDisposable
{
    private readonly ImGuiTextInputQueue _textInputQueue = new();
    private IntPtr _imGuiContext;
    private int _fontTexture;
    private Shader _shaderObj;
    private int _shader;
    private int _projMtxLocation;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _indexBuffer;
    private Vector2i _currentSize;
    private bool _wasFocused = true;

    public StartupMenuRenderer(Vector2i initialSize)
    {
        _currentSize = initialSize;
        InitializeImGui();
    }

    public void Resize(Vector2i size)
    {
        _currentSize = size;
    }

    public void QueueTextInput(uint unicode)
    {
        _textInputQueue.Enqueue(unicode);
    }

    public ConnectionUiRequest DrawFrame(
        FrameEventArgs args,
        bool windowFocused,
        MouseState mouseState,
        KeyboardState keyboardState,
        string host,
        string portText,
        string playerName,
        string localServerStatus,
        string connectionStatus,
        bool isBusy,
        IReadOnlyList<CharacterListEntry>? characters,
        out string? pickedCharacter,
        out bool cancelPickerPressed,
        bool showBack = false)
    {
        BeginFrame(args, windowFocused, mouseState, keyboardState);
        pickedCharacter = null;
        cancelPickerPressed = false;

        string hostBuffer = host;
        string portBuffer = portText;
        string playerNameBuffer = playerName;
        bool useLocalServerPressed = false;
        bool connectPressed = false;
        bool backPressed = false;

        Vector2i safeViewport = GetSafeViewportSize(_currentSize);
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(safeViewport.X * 0.5f, safeViewport.Y * 0.5f), ImGuiCond.Always, new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(460f, 0f), ImGuiCond.Always);

        ImGui.Begin(
            "Multiplayer Startup",
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove);

        ImGui.TextWrapped("Connect to a shared world or use the integrated local server that starts automatically with the desktop client.");
        ImGui.Separator();
        ImGui.TextWrapped(localServerStatus);
        ImGui.TextWrapped(connectionStatus);
        ImGui.Spacing();

        if (characters is { Count: > 0 })
        {
            // M1-C: the account's characters on this server. Pick one to join as it, or type a new
            // name below and Connect.
            ImGui.Text("Your characters");
            for (int i = 0; i < characters.Count; i++)
            {
                CharacterListEntry entry = characters[i];
                if (ImGui.Button($"{entry.DisplayName}  (level {entry.Level})##character{i}", new System.Numerics.Vector2(-1f, 0f)))
                {
                    pickedCharacter = entry.DisplayName;
                }
            }

            if (ImGui.Button("Cancel"))
            {
                cancelPickerPressed = true;
            }

            ImGui.Separator();
            ImGui.TextWrapped("Or enter a new name:");
        }

        ImGui.InputText("Host", ref hostBuffer, 128);
        ImGui.InputText("Port", ref portBuffer, 16);
        ImGui.InputText("Player Name", ref playerNameBuffer, 64);

        if (isBusy)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Use Local Server"))
        {
            useLocalServerPressed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Connect"))
        {
            connectPressed = true;
        }

        if (showBack)
        {
            // Disabled while a connect is in flight, so no connected session is left behind.
            ImGui.SameLine();
            if (ImGui.Button("Back"))
            {
                backPressed = true;
            }
        }

        if (isBusy)
        {
            ImGui.EndDisabled();
        }

        ImGui.End();

        ImGui.Render();
        RenderImGuiDrawData(ImGui.GetDrawData());

        return new ConnectionUiRequest(
            hostBuffer,
            portBuffer,
            playerNameBuffer,
            useLocalServerPressed,
            connectPressed,
            backPressed);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GL.DeleteTexture(_fontTexture);
        _shaderObj.Dispose();
        GL.DeleteBuffer(_vertexBuffer);
        GL.DeleteBuffer(_indexBuffer);
        GL.DeleteVertexArray(_vertexArray);
        ImGui.DestroyContext(_imGuiContext);
    }

    [MemberNotNull(nameof(_shaderObj))]
    private void InitializeImGui()
    {
        _imGuiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imGuiContext);

        var io = ImGui.GetIO();
        io.Fonts.AddFontDefault();
        Vector2i safeViewportSize = GetSafeViewportSize(_currentSize);
        io.DisplaySize = new System.Numerics.Vector2(safeViewportSize.X, safeViewportSize.Y);
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        io.ConfigWindowsResizeFromEdges = false;

        CreateFontTexture();
        CreateImGuiResources();
    }

    private void CreateFontTexture()
    {
        ImGui.GetIO().Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
        _fontTexture = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        // GetTexDataAsRGBA32 returns R,G,B,A bytes.
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
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

    private void BeginFrame(
        FrameEventArgs args,
        bool windowFocused,
        MouseState mouseState,
        KeyboardState keyboardState)
    {
        ImGui.SetCurrentContext(_imGuiContext);
        var io = ImGui.GetIO();
        Vector2i safeViewportSize = GetSafeViewportSize(_currentSize);
        io.DisplaySize = new System.Numerics.Vector2(safeViewportSize.X, safeViewportSize.Y);
        io.DeltaTime = (float)args.Time;
        io.MouseDrawCursor = true;
        io.MousePos = new System.Numerics.Vector2(mouseState.Position.X, mouseState.Position.Y);
        io.MouseDown[0] = mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left);
        io.MouseDown[1] = mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right);
        ApplyKeyboardState(io, keyboardState);
        foreach (uint unicode in _textInputQueue.Drain())
        {
            io.AddInputCharacter(unicode);
        }

        if (windowFocused && !_wasFocused)
        {
            io.MousePos = new System.Numerics.Vector2(-float.MaxValue, -float.MaxValue);
        }

        _wasFocused = windowFocused;
        ImGui.NewFrame();
    }

    private static void ApplyKeyboardState(ImGuiIOPtr io, KeyboardState keyboardState)
    {
        io.KeyCtrl = keyboardState.IsKeyDown(Keys.LeftControl) || keyboardState.IsKeyDown(Keys.RightControl);
        io.KeyShift = keyboardState.IsKeyDown(Keys.LeftShift) || keyboardState.IsKeyDown(Keys.RightShift);
        io.KeyAlt = keyboardState.IsKeyDown(Keys.LeftAlt) || keyboardState.IsKeyDown(Keys.RightAlt);
        io.KeySuper = keyboardState.IsKeyDown(Keys.LeftSuper) || keyboardState.IsKeyDown(Keys.RightSuper);

        AddKey(io, ImGuiKey.Backspace, keyboardState.IsKeyDown(Keys.Backspace));
        AddKey(io, ImGuiKey.Delete, keyboardState.IsKeyDown(Keys.Delete));
        AddKey(io, ImGuiKey.LeftArrow, keyboardState.IsKeyDown(Keys.Left));
        AddKey(io, ImGuiKey.RightArrow, keyboardState.IsKeyDown(Keys.Right));
        AddKey(io, ImGuiKey.UpArrow, keyboardState.IsKeyDown(Keys.Up));
        AddKey(io, ImGuiKey.DownArrow, keyboardState.IsKeyDown(Keys.Down));
        AddKey(io, ImGuiKey.Home, keyboardState.IsKeyDown(Keys.Home));
        AddKey(io, ImGuiKey.End, keyboardState.IsKeyDown(Keys.End));
        AddKey(io, ImGuiKey.Enter, keyboardState.IsKeyDown(Keys.Enter));
        AddKey(io, ImGuiKey.Tab, keyboardState.IsKeyDown(Keys.Tab));
        AddKey(io, ImGuiKey.Escape, keyboardState.IsKeyDown(Keys.Escape));
    }

    private static void AddKey(ImGuiIOPtr io, ImGuiKey key, bool isDown)
    {
        io.AddKeyEvent(key, isDown);
    }

    // Draws ImGui with its own blend, cull, depth and scissor state on texture unit 0, then
    // restores the state it found.
    private void RenderImGuiDrawData(ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount == 0)
        {
            return;
        }

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
        Matrix4 mvp = Matrix4.CreateOrthographicOffCenter(
            displayPos.X,
            displayPos.X + displaySize.X,
            displayPos.Y + displaySize.Y,
            displayPos.Y,
            -1f,
            1f);

        GL.UseProgram(_shader);
        GL.UniformMatrix4(_projMtxLocation, false, ref mvp);
        GL.BindVertexArray(_vertexArray);

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>(), cmdList.VtxBuffer.Data, BufferUsageHint.StreamDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferData(BufferTarget.ElementArrayBuffer, cmdList.IdxBuffer.Size * sizeof(ushort), cmdList.IdxBuffer.Data, BufferUsageHint.StreamDraw);

            for (int cmdIndex = 0; cmdIndex < cmdList.CmdBuffer.Size; cmdIndex++)
            {
                var command = cmdList.CmdBuffer[cmdIndex];
                if (command.UserCallback != IntPtr.Zero)
                {
                    continue;
                }

                if (!ImGuiScissorRect.TryFromClipRect(
                    command.ClipRect,
                    displayPos,
                    framebufferScale,
                    framebufferHeight,
                    out ImGuiScissorRect scissor))
                {
                    continue;
                }

                GL.BindTexture(TextureTarget.Texture2D, (int)command.TextureId);
                GL.Scissor(scissor.X, scissor.Y, scissor.Width, scissor.Height);

                GL.DrawElements(
                    PrimitiveType.Triangles,
                    (int)command.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (int)command.IdxOffset * sizeof(ushort));
                RenderCounters.CountDraw();
            }
        }

        previousState.Restore();
    }

    private static Vector2i GetSafeViewportSize(Vector2i size)
    {
        return new Vector2i(Math.Max(size.X, 1), Math.Max(size.Y, 1));
    }
}
