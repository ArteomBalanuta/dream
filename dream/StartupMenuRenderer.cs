using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Runtime.CompilerServices;

public sealed class StartupMenuRenderer : IDisposable
{
    private readonly ImGuiTextInputQueue _textInputQueue = new();
    private IntPtr _imGuiContext;
    private int _fontTexture;
    private Shader _shaderObj;
    private int _shader;
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
        bool isBusy)
    {
        BeginFrame(args, windowFocused, mouseState, keyboardState);

        string hostBuffer = host;
        string portBuffer = portText;
        string playerNameBuffer = playerName;
        bool useLocalServerPressed = false;
        bool connectPressed = false;

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

        ImGui.InputText("Host", ref hostBuffer, 128);
        ImGui.InputText("Port", ref portBuffer, 16);
        ImGui.InputText("Player Name", ref playerNameBuffer, 64);

        if (ImGui.Button("Use Local Server"))
        {
            useLocalServerPressed = true;
        }

        ImGui.SameLine();
        if (isBusy)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Connect"))
        {
            connectPressed = true;
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
            connectPressed);
    }

    public void Dispose()
    {
        GL.DeleteTexture(_fontTexture);
        _shaderObj.Dispose();
        GL.DeleteBuffer(_vertexBuffer);
        GL.DeleteBuffer(_indexBuffer);
        GL.DeleteVertexArray(_vertexArray);
        ImGui.DestroyContext(_imGuiContext);
    }

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
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        ImGui.GetIO().Fonts.SetTexID((IntPtr)_fontTexture);
    }

    private void CreateImGuiResources()
    {
        _shaderObj = new Shader(
            AssetPaths.Shader("hudVertexText.glsl"),
            AssetPaths.Shader("hudFragmentText.glsl"));
        _shader = _shaderObj.Handle;
        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        _indexBuffer = GL.GenBuffer();
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

    private void RenderImGuiDrawData(ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount == 0)
        {
            return;
        }

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);

        var io = ImGui.GetIO();
        Matrix4 mvp = Matrix4.CreateOrthographicOffCenter(
            0f,
            io.DisplaySize.X,
            io.DisplaySize.Y,
            0f,
            -1f,
            1f);

        GL.UseProgram(_shader);
        GL.UniformMatrix4(GL.GetUniformLocation(_shader, "ProjMtx"), false, ref mvp);
        GL.Uniform1(GL.GetUniformLocation(_shader, "Texture"), 0);
        GL.BindVertexArray(_vertexArray);

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>(), cmdList.VtxBuffer.Data, BufferUsageHint.StreamDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferData(BufferTarget.ElementArrayBuffer, cmdList.IdxBuffer.Size * sizeof(ushort), cmdList.IdxBuffer.Data, BufferUsageHint.StreamDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, Unsafe.SizeOf<ImDrawVert>(), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, Unsafe.SizeOf<ImDrawVert>(), 8);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, Unsafe.SizeOf<ImDrawVert>(), 16);

            for (int cmdIndex = 0; cmdIndex < cmdList.CmdBuffer.Size; cmdIndex++)
            {
                var command = cmdList.CmdBuffer[cmdIndex];
                if (command.UserCallback != IntPtr.Zero)
                {
                    continue;
                }

                GL.BindTexture(TextureTarget.Texture2D, (int)command.TextureId);
                GL.Scissor(
                    (int)command.ClipRect.X,
                    (int)(io.DisplaySize.Y - command.ClipRect.W),
                    (int)(command.ClipRect.Z - command.ClipRect.X),
                    (int)(command.ClipRect.W - command.ClipRect.Y));

                GL.DrawElements(
                    PrimitiveType.Triangles,
                    (int)command.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (int)command.IdxOffset * sizeof(ushort));
            }
        }

        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Disable(EnableCap.Blend);
    }

    private static Vector2i GetSafeViewportSize(Vector2i size)
    {
        return new Vector2i(Math.Max(size.X, 1), Math.Max(size.Y, 1));
    }
}
