using Dream.Application;
using Dream.Assets;
using Dream.Client.Presentation;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Actors;
using Dream.Rendering.Hud;
using Dream.Rendering.Models;
using Dream.Rendering.ViewModels;
using Dream.Rendering.World;
using Dream.Simulation.Enemies;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.Core;

﻿using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ImGuiNET;
using System.Runtime.CompilerServices;
public readonly record struct HudFrameResult(
    HudInteractionRequest Request);

public class Render
{
    private readonly List<FloatingCombatTextRenderState> _floatingCombatTextStates = new();

    private int _vertexArrayObject;
    private int _vertexBufferObject;
    private Dictionary<string, Shader> _shaders = new();
    private Matrix4 _projection;

    // Cube vertex data: note that you might want to use indexed drawing.
    private readonly float[] _cubeVertices = {
    // Front face
    -0.5f, -0.5f,  0.5f,  // Bottom-left
     0.5f, -0.5f,  0.5f,  // Bottom-right
     0.5f,  0.5f,  0.5f,  // Top-right
    -0.5f,  0.5f,  0.5f,  // Top-left

    // Back face
    -0.5f, -0.5f, -0.5f,  // Bottom-left
     0.5f, -0.5f, -0.5f,  // Bottom-right
     0.5f,  0.5f, -0.5f,  // Top-right
    -0.5f,  0.5f, -0.5f,  // Top-left

    // Left face
    -0.5f, -0.5f, -0.5f,  // Bottom-left
    -0.5f, -0.5f,  0.5f,  // Bottom-right
    -0.5f,  0.5f,  0.5f,  // Top-right
    -0.5f,  0.5f, -0.5f,  // Top-left

    // Right face
     0.5f, -0.5f, -0.5f,  // Bottom-left
     0.5f, -0.5f,  0.5f,  // Bottom-right
     0.5f,  0.5f,  0.5f,  // Top-right
     0.5f,  0.5f, -0.5f,  // Top-left

    // Top face
    -0.5f,  0.5f,  0.5f,  // Bottom-left
     0.5f,  0.5f,  0.5f,  // Bottom-right
     0.5f,  0.5f, -0.5f,  // Top-right
    -0.5f,  0.5f, -0.5f,  // Top-left

    // Bottom face
    -0.5f, -0.5f,  0.5f,  // Bottom-left
     0.5f, -0.5f,  0.5f,  // Bottom-right
     0.5f, -0.5f, -0.5f,  // Top-right
    -0.5f, -0.5f, -0.5f   // Top-left
    };

    private int _hudVao;
    private int _hudVbo;
    private Shader _hudShader;

    private IntPtr _imGuiContext;
    private int _fontTexture;
    private Shader _shaderObj;
    private int _shader;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _indexBuffer;
    private Vector2i _currentSize;
    private Vector2i _crosshairViewportSize;
    private readonly WorldRenderer _worldRenderer;
    private readonly EnemyRenderer _enemyRenderer;
    private readonly LocalPlayerRenderer _localPlayerRenderer;
    private readonly RemotePlayerRenderer _remotePlayerRenderer;
    private readonly DebugRenderer _debugRenderer;
    private readonly HudRenderer _hudRenderer;
    private readonly ViewModelRenderer _viewModelRenderer;

    private void InitializeShaders()
    {
        LoadShader("default",
            AssetPaths.Shader("vertexShader.glsl"),
            AssetPaths.Shader("fragmentShader.glsl"));

        LoadShader("cube",
            AssetPaths.Shader("cubeVertex.glsl"),
            AssetPaths.Shader("cubeFragment.glsl"));

        LoadShader("enemy",
            AssetPaths.Shader("cubeVertex.glsl"),
            AssetPaths.Shader("enemyFragment.glsl"));
    }

    private void InitializeHUD()
    {
        _hudVao = GL.GenVertexArray();
        _hudVbo = GL.GenBuffer();
        GL.BindVertexArray(_hudVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _hudVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, 0, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);

        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);

        _hudShader = new Shader(
            AssetPaths.Shader("hudVertex.glsl"),
            AssetPaths.Shader("hudFragment.glsl"));

        UpdateCrosshairGeometry();
        InitializeImGui();
    }

    private void UpdateCrosshairGeometry()
    {
        if (_hudVbo == 0)
        {
            return;
        }

        _crosshairViewportSize = _currentSize;

        if (_currentSize.X <= 0 || _currentSize.Y <= 0)
        {
            return;
        }

        float[] vertices = HudLayout.CreateCrosshair(_currentSize.X, _currentSize.Y).ToVertexArray();

        GL.BindBuffer(BufferTarget.ArrayBuffer, _hudVbo);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            vertices.Length * sizeof(float),
            vertices,
            BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
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
        ImGui.GetIO().Fonts.GetTexDataAsRGBA32(out IntPtr pixels,
            out int width, out int height, out _);

        _fontTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
            width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);

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

    private void RenderImGuiDrawData(ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount == 0) return;

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);

        var io = ImGui.GetIO();
        var mvp = Matrix4.CreateOrthographicOffCenter(
            0.0f,
            io.DisplaySize.X,
            io.DisplaySize.Y,
            0.0f,
            -1.0f, 1.0f);

        GL.UseProgram(_shader);
        GL.UniformMatrix4(GL.GetUniformLocation(_shader, "ProjMtx"), false, ref mvp);
        GL.Uniform1(GL.GetUniformLocation(_shader, "Texture"), 0);

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

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false,
                Unsafe.SizeOf<ImDrawVert>(), 0);

            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false,
                Unsafe.SizeOf<ImDrawVert>(), 8);

            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true,
                Unsafe.SizeOf<ImDrawVert>(), 16);

            for (int cmd_i = 0; cmd_i < cmdList.CmdBuffer.Size; cmd_i++)
            {
                var pcmd = cmdList.CmdBuffer[cmd_i];
                if (pcmd.UserCallback != IntPtr.Zero) continue;

                GL.BindTexture(TextureTarget.Texture2D, (int)pcmd.TextureId);
                GL.Scissor(
                    (int)pcmd.ClipRect.X,
                    (int)(io.DisplaySize.Y - pcmd.ClipRect.W),
                    (int)(pcmd.ClipRect.Z - pcmd.ClipRect.X),
                    (int)(pcmd.ClipRect.W - pcmd.ClipRect.Y));

                GL.DrawElements(PrimitiveType.Triangles,
                    (int)pcmd.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (int)pcmd.IdxOffset * sizeof(ushort));
            }
        }

        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Disable(EnableCap.Blend);
    }

    public void DisposeHUD()
    {
        GL.DeleteTexture(_fontTexture);
        _hudShader.Dispose();
        _shaderObj.Dispose();
        GL.DeleteBuffer(_hudVbo);
        GL.DeleteBuffer(_vertexBuffer);
        GL.DeleteBuffer(_indexBuffer);
        GL.DeleteVertexArray(_hudVao);
        GL.DeleteVertexArray(_vertexArray);
        ImGui.DestroyContext(_imGuiContext);
    }

    private static Vector2i GetSafeViewportSize(Vector2i size)
    {
        return new Vector2i(Math.Max(size.X, 1), Math.Max(size.Y, 1));
    }

    private void UpdateProjection(int width, int height, float fieldOfViewDegrees)
    {
        _currentSize = new Vector2i(width, height);
        Vector2i safeViewportSize = GetSafeViewportSize(_currentSize);
        float aspectRatio = (float)safeViewportSize.X / safeViewportSize.Y;

        _projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(fieldOfViewDegrees),
            aspectRatio,
            0.1f,
            100.0f);

        if (_currentSize != _crosshairViewportSize)
        {
            UpdateCrosshairGeometry();
        }
    }

    public void UpdateProjectionForSettings(GameSettings settings)
    {
        UpdateProjection(settings.WindowWidth, settings.WindowHeight, settings.FieldOfViewDegrees);
    }

    public Render(GameSettings settings)
    {
        _worldRenderer = new WorldRenderer(this);
        _enemyRenderer = new EnemyRenderer(this);
        _localPlayerRenderer = new LocalPlayerRenderer(this);
        _remotePlayerRenderer = new RemotePlayerRenderer(this);
        _debugRenderer = new DebugRenderer(this);
        _hudRenderer = new HudRenderer(this);
        _viewModelRenderer = new ViewModelRenderer(this);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        InitializeShaders();

        UpdateProjectionForSettings(settings);

        // Set up VAO and VBO for the cube.
        _vertexArrayObject = GL.GenVertexArray();
        _vertexBufferObject = GL.GenBuffer();

        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, _cubeVertices.Length * sizeof(float), _cubeVertices, BufferUsageHint.StaticDraw);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);

        InitializeHUD();
    }

    public WorldRenderer World => _worldRenderer;

    public EnemyRenderer Enemies => _enemyRenderer;

    public DebugRenderer Debug => _debugRenderer;

    public HudRenderer Hud => _hudRenderer;

    public ViewModelRenderer ViewModels => _viewModelRenderer;

    public LocalPlayerRenderer LocalPlayers => _localPlayerRenderer;

    public RemotePlayerRenderer RemotePlayers => _remotePlayerRenderer;

    public Vector2i ViewportSize => GetSafeViewportSize(_currentSize);

    public Matrix4 ProjectionMatrix => _projection;

    public Shader GetShader(string name)
    {
        return _shaders[name];
    }

    public void LoadShader(string name, string vertexPath, string fragmentPath)
    {
        _shaders[name] = new Shader(vertexPath, fragmentPath);
    }

    // Common method to set transformation uniforms.
    private void SetTransforms(Shader shader, Matrix4 model, Matrix4 view)
    {
        shader.SetMatrix4("model", model);
        shader.SetMatrix4("view", view);
        shader.SetMatrix4("projection", _projection);
    }

    private void DrawCubeInternal(Shader shader, Matrix4 model, Matrix4 view)
    {
        shader.Use();
        SetTransforms(shader, model, view);
        GL.BindVertexArray(_vertexArrayObject);
        DrawCubeFaceFans();
        GL.BindVertexArray(0);
    }

    public void DrawCube(Camera camera, Cube cube)
    {
        float assetScale = ResolveCubeAssetScale(cube.Animation);
        Matrix4 modelMatrix = CubeTransforms.CreateCenteredVisualModelMatrix(cube.Position, cube.Side, assetScale);

        // Use the cube's pre-calculated center position directly
        if (cube.Animation != null)
        {
            cube.Animation.Render(_shaders[cube.ShaderName],
                                camera.GetViewMatrix(),
                                _projection,
                                modelMatrix);
        }
        else
        {
            DrawCubeTransparent(camera, modelMatrix);
            DrawCubeWireframe(camera, modelMatrix);
        }
    }

    public void DrawGeometryBlock(Camera camera, GeometryRenderBlockDescriptor block)
    {
        float assetScale = ResolveCubeAssetScale(block.Animation);
        Matrix4 modelMatrix = CubeTransforms.CreateCenteredVisualModelMatrix(block.Center, block.VisualSize, assetScale);

        if (block.Animation != null)
        {
            block.Animation.Render(_shaders[block.ShaderName],
                camera.GetViewMatrix(),
                _projection,
                modelMatrix);
        }
        else
        {
            DrawCubeTransparent(camera, modelMatrix);
            DrawCubeWireframe(camera, modelMatrix);
        }
    }

    public void DrawGeometryBlockBatch(
        Camera camera,
        AnimatedModel animation,
        string shaderName,
        IReadOnlyList<GeometryRenderBlockDescriptor> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        Shader shader = GetShader(shaderName);
        Matrix4 view = camera.GetViewMatrix();
        Matrix4[] modelMatrices = new Matrix4[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            GeometryRenderBlockDescriptor block = blocks[i];
            float assetScale = ResolveCubeAssetScale(block.Animation);
            modelMatrices[i] = CubeTransforms.CreateCenteredVisualModelMatrix(
                block.Center,
                block.VisualSize,
                assetScale);
        }

        animation.BeginRender(shader, view, _projection);
        animation.RenderPreparedInstanced(shader, modelMatrices);
        animation.EndRender();
    }


    private void DrawCubeWireframe(Camera camera, Matrix4 modelMatrix)
    {
        Shader shader = _shaders.TryGetValue("default", out var s) ? s : _shaders["default"];
        shader.Use();
        shader.SetVector3("color", new Vector3(1.0f, 1.0f, 1.0f)); // white
        SetTransforms(shader, modelMatrix, camera.GetViewMatrix());

        // Enable wireframe mode.
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
        GL.BindVertexArray(_vertexArrayObject);
        DrawCubeFaceFans();
        GL.BindVertexArray(0);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
    }

    private void DrawCubeTransparent(Camera camera, Matrix4 modelMatrix)
    {
        Shader shader = _shaders.TryGetValue("default", out var s) ? s : _shaders["default"];
        shader.Use();
        shader.SetVector3("color", new Vector3(0.5f, 0.7f, 1.0f)); // light blue
        SetTransforms(shader, modelMatrix, camera.GetViewMatrix());

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.BindVertexArray(_vertexArrayObject);
        DrawCubeFaceFans();
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
    }

    public void DrawCollisionShape(Matrix4 modelMatrix, Camera camera)
    {
        Shader shader = _shaders["default"];
        shader.Use();
        shader.SetVector3("color", new Vector3(1.0f, 0.0f, 0.0f)); // Red color for collision

        SetTransforms(shader, modelMatrix, camera.GetViewMatrix());

        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
        GL.BindVertexArray(_vertexArrayObject);
        DrawCubeFaceFans();
        GL.BindVertexArray(0);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
    }

    public void DrawSolidCube(Camera camera, Matrix4 modelMatrix, Vector3 color)
    {
        Shader shader = _shaders.TryGetValue("default", out var found) ? found : _shaders["default"];
        shader.Use();
        shader.SetVector3("color", color);
        SetTransforms(shader, modelMatrix, camera.GetViewMatrix());

        GL.BindVertexArray(_vertexArrayObject);
        DrawCubeFaceFans();
        GL.BindVertexArray(0);
    }

    private void DrawCubeFaceFans()
    {
        foreach ((int first, int count) in CreateCubeFaceDrawRanges(_cubeVertices.Length / 3))
        {
            GL.DrawArrays(PrimitiveType.TriangleFan, first, count);
        }
    }

    private static (int First, int Count)[] CreateCubeFaceDrawRanges(int vertexCount)
    {
        const int faceVertexCount = 4;
        if (vertexCount % faceVertexCount != 0)
        {
            throw new ArgumentException("Cube vertex count must contain complete four-vertex faces.", nameof(vertexCount));
        }

        (int First, int Count)[] ranges = new (int First, int Count)[vertexCount / faceVertexCount];
        for (int face = 0; face < ranges.Length; face++)
        {
            ranges[face] = (face * faceVertexCount, faceVertexCount);
        }

        return ranges;
    }

    public void DrawCollisionBody(BulletSharp.RigidBody body, Camera camera)
    {
        if (body.MotionState == null)
        {
            return;
        }

        body.MotionState.GetWorldTransform(out BulletSharp.Math.Matrix transform);
        if (body.CollisionShape is BulletSharp.BoxShape boxShape)
        {
            DrawCollisionShape(
                CubeTransforms.CreateCollisionDebugModelMatrix(
                    transform,
                    boxShape.HalfExtentsWithMargin.ToOpenTK()),
                camera);
            return;
        }

        DrawCollisionShape(CubeTransforms.CreateCollisionDebugModelMatrix(transform), camera);
    }

    private bool _wasFocused = true;
    public HudFrameResult DrawHUD(
        FrameEventArgs args,
        bool windowFocused,
        GameSettings settings,
        MouseState mouseState,
        Camera camera,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        IReadOnlyList<EnemyActor> activeEnemies,
        IReadOnlyList<PlayerRosterEntry> playerRoster,
        CombatFeedbackSystem combatFeedback,
        string? rejectionMessage,
        HudStatusSnapshot status,
        string? connectionLabel = null)
    {
        GL.Disable(EnableCap.DepthTest);
        DrawCrosshair(settings);
        BeginHudFrame(args, windowFocused, settings, mouseState);
        DrawFloatingCombatText(camera, combatFeedback, settings.FloatingCombatText);
        DrawWorldActorLabels(camera, remotePlayers, activeEnemies);
        HudInteractionRequest request = DrawHudInterface(settings, status, rejectionMessage, connectionLabel, playerRoster);
        ImGui.Render();
        RenderImGuiDrawData(ImGui.GetDrawData());
        GL.Enable(EnableCap.DepthTest);

        return new HudFrameResult(request);
    }

    private void DrawFloatingCombatText(
        Camera camera,
        CombatFeedbackSystem combatFeedback,
        FloatingCombatTextTuning tuning)
    {
        combatFeedback.CollectRenderStates(_floatingCombatTextStates, tuning);
        if (_floatingCombatTextStates.Count == 0)
        {
            return;
        }

        ImDrawListPtr drawList = ImGui.GetBackgroundDrawList();
        ImFontPtr font = ImGui.GetFont();
        float baseFontSize = ImGui.GetFontSize();

        foreach (FloatingCombatTextRenderState state in _floatingCombatTextStates)
        {
            Vector3 worldPosition =
                state.WorldAnchor +
                (camera.Right * state.HorizontalOffset) +
                (camera.Up * state.VerticalOffset) -
                (camera.Front * state.DepthOffset);

            if (!TryProjectWorldToScreen(camera, worldPosition, out System.Numerics.Vector2 screenPosition))
            {
                continue;
            }

            uint shadow = ToImGuiColor(new Vector4(0f, 0f, 0f, state.Alpha));
            uint color = ToImGuiColor(state.Style.Color with { W = state.Alpha });
            float size = baseFontSize * state.Scale;
            float outline = state.Style.OutlineThickness;

            drawList.AddText(font, size, new System.Numerics.Vector2(screenPosition.X - outline, screenPosition.Y), shadow, state.Text);
            drawList.AddText(font, size, new System.Numerics.Vector2(screenPosition.X + outline, screenPosition.Y), shadow, state.Text);
            drawList.AddText(font, size, new System.Numerics.Vector2(screenPosition.X, screenPosition.Y - outline), shadow, state.Text);
            drawList.AddText(font, size, new System.Numerics.Vector2(screenPosition.X, screenPosition.Y + outline), shadow, state.Text);
            drawList.AddText(font, size, screenPosition, color, state.Text);
        }
    }

    private void DrawWorldActorLabels(
        Camera camera,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        IReadOnlyList<EnemyActor> activeEnemies)
    {
        IReadOnlyList<WorldActorLabelState> labels =
            [.. ActorWorldLabelBuilder.CreateRemotePlayerLabels(remotePlayers), .. ActorWorldLabelBuilder.CreateEnemyLabels(activeEnemies)];
        if (labels.Count == 0)
        {
            return;
        }

        ImDrawListPtr drawList = ImGui.GetBackgroundDrawList();
        ImFontPtr font = ImGui.GetFont();
        float baseFontSize = ImGui.GetFontSize();
        uint nameColor = ToImGuiColor(new Vector4(1f, 0.95f, 0.8f, 1f));
        uint healthColor = ToImGuiColor(new Vector4(1f, 0.45f, 0.35f, 1f));
        uint shadowColor = ToImGuiColor(new Vector4(0f, 0f, 0f, 0.9f));

        foreach (WorldActorLabelState label in labels)
        {
            if (!TryProjectWorldToScreen(camera, label.WorldAnchor, out System.Numerics.Vector2 screenPosition))
            {
                continue;
            }

            DrawOutlinedText(drawList, font, baseFontSize, screenPosition, shadowColor, nameColor, label.DisplayName);
            DrawOutlinedText(
                drawList,
                font,
                baseFontSize * 0.92f,
                new System.Numerics.Vector2(screenPosition.X, screenPosition.Y + (baseFontSize * 1.05f)),
                shadowColor,
                healthColor,
                label.HealthLabel);
        }
    }

    private static void DrawOutlinedText(
        ImDrawListPtr drawList,
        ImFontPtr font,
        float fontSize,
        System.Numerics.Vector2 position,
        uint shadowColor,
        uint fillColor,
        string text)
    {
        System.Numerics.Vector2 size = ImGui.CalcTextSize(text);
        System.Numerics.Vector2 centered = new(position.X - (size.X * 0.5f), position.Y);
        const float outline = 1f;
        drawList.AddText(font, fontSize, new System.Numerics.Vector2(centered.X - outline, centered.Y), shadowColor, text);
        drawList.AddText(font, fontSize, new System.Numerics.Vector2(centered.X + outline, centered.Y), shadowColor, text);
        drawList.AddText(font, fontSize, new System.Numerics.Vector2(centered.X, centered.Y - outline), shadowColor, text);
        drawList.AddText(font, fontSize, new System.Numerics.Vector2(centered.X, centered.Y + outline), shadowColor, text);
        drawList.AddText(font, fontSize, centered, fillColor, text);
    }

    private bool TryProjectWorldToScreen(Camera camera, Vector3 worldPosition, out System.Numerics.Vector2 screenPosition)
    {
        Matrix4 viewProjection = camera.GetViewMatrix() * _projection;
        Vector4 clipPosition = Vector4.TransformRow(new Vector4(worldPosition.X, worldPosition.Y, worldPosition.Z, 1f), viewProjection);

        if (clipPosition.W <= 0f)
        {
            screenPosition = default;
            return false;
        }

        Vector3 ndc = clipPosition.Xyz / clipPosition.W;
        if (ndc.Z < -1f || ndc.Z > 1f)
        {
            screenPosition = default;
            return false;
        }

        Vector2i safeViewportSize = GetSafeViewportSize(_currentSize);
        float x = ((ndc.X + 1f) * 0.5f) * safeViewportSize.X;
        float y = ((1f - ndc.Y) * 0.5f) * safeViewportSize.Y;
        screenPosition = new System.Numerics.Vector2(x, y);
        return true;
    }

    private static uint ToImGuiColor(Vector4 color)
    {
        byte r = (byte)Math.Clamp((int)(color.X * 255f), 0, 255);
        byte g = (byte)Math.Clamp((int)(color.Y * 255f), 0, 255);
        byte b = (byte)Math.Clamp((int)(color.Z * 255f), 0, 255);
        byte a = (byte)Math.Clamp((int)(color.W * 255f), 0, 255);
        return (uint)((a << 24) | (b << 16) | (g << 8) | r);
    }

    private void DrawCrosshair(GameSettings settings)
    {
        if (settings.SettingsVisible || _currentSize.X <= 0 || _currentSize.Y <= 0)
        {
            return;
        }

        _hudShader.Use();
        Vector2i safeViewportSize = GetSafeViewportSize(_currentSize);
        Matrix4 projection = Matrix4.CreateOrthographicOffCenter(0, safeViewportSize.X, safeViewportSize.Y, 0, -1, 1);
        _hudShader.SetMatrix4("projection", projection);
        _hudShader.SetVector3("color", new Vector3(1.0f, 0.0f, 0.0f));

        GL.BindVertexArray(_hudVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, 8);
        GL.BindVertexArray(0);
    }

    private void BeginHudFrame(FrameEventArgs args, bool windowFocused, GameSettings settings, MouseState mouseState)
    {
        var io = ImGui.GetIO();
        Vector2i safeViewportSize = GetSafeViewportSize(_currentSize);

        io.DisplaySize = new System.Numerics.Vector2(safeViewportSize.X, safeViewportSize.Y);
        io.DeltaTime = (float)args.Time;
        io.MouseDrawCursor = settings.SettingsVisible;
        io.MousePos = settings.SettingsVisible
            ? new System.Numerics.Vector2(mouseState.Position.X, mouseState.Position.Y)
            : new System.Numerics.Vector2(-float.MaxValue, -float.MaxValue);
        io.MouseDown[0] = settings.SettingsVisible && mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left);
        io.MouseDown[1] = settings.SettingsVisible && mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right);

        // Reset input states when regaining focus
        if (windowFocused && !_wasFocused)
        {
            // Clear all keyboard/mouse states
            io.KeyCtrl = false;
            io.KeyShift = false;
            io.KeyAlt = false;
            io.KeySuper = false;

            // Clear mouse positions
            io.MousePos = new System.Numerics.Vector2(-float.MaxValue, -float.MaxValue);
        }
        _wasFocused = windowFocused;

        ImGui.NewFrame();
    }

    private static HudInteractionRequest DrawHudInterface(
        GameSettings settings,
        HudStatusSnapshot status,
        string? rejectionMessage,
        string? connectionLabel,
        IReadOnlyList<PlayerRosterEntry> playerRoster)
    {
        float? fieldOfViewDegrees = null;
        float? mouseSensitivity = null;
        CameraViewMode? cameraViewMode = null;
        float? noclipSpeed = null;
        Vector2i? requestedResolution = null;
        int? stagedFloorSize = null;
        int? requestedFloorSize = null;
        bool? showCollisionGeometry = null;
        int? strength = null;
        float? attackSpeed = null;
        int? vitality = null;
        FloatingCombatTextTuning? floatingCombatText = null;

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10, 10));
        ImGui.Begin("HUD",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoMove |
            (settings.SettingsVisible ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoInputs) |
            ImGuiWindowFlags.NoFocusOnAppearing );

        ImGui.Text(status.HealthLabel);
        ImGui.Text(status.ModeLabel);
        ImGui.Text(status.DamageLabel);
        if (!string.IsNullOrWhiteSpace(connectionLabel))
        {
            ImGui.Text($"Server: {connectionLabel}");
        }
        if (playerRoster.Count > 0)
        {
            ImGui.Text($"Players: {playerRoster.Count}");
        }
        if (!string.IsNullOrWhiteSpace(rejectionMessage))
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.35f, 1f), rejectionMessage);
        }
        ImGui.Text($"Resolution: {settings.WindowWidth} x {settings.WindowHeight}");
        ImGui.Text("Press Esc to toggle settings");
        ImGui.End();

        if (settings.SettingsVisible)
        {
            ImGui.SetNextWindowPos(new System.Numerics.Vector2(20, 70), ImGuiCond.FirstUseEver);
            ImGui.Begin("Settings", ImGuiWindowFlags.AlwaysAutoResize);

            if (!string.IsNullOrWhiteSpace(connectionLabel))
            {
                ImGui.Text($"Connected Server: {connectionLabel}");
            }

            if (playerRoster.Count > 0 && ImGui.CollapsingHeader("Connected Players", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (PlayerRosterEntry player in playerRoster)
                {
                    ImGui.Text($"{player.DisplayName} [{player.SessionId}]");
                }
            }

            float fieldOfView = settings.FieldOfViewDegrees;
            if (ImGui.SliderFloat("Field Of View", ref fieldOfView, 50f, 100f))
            {
                fieldOfViewDegrees = fieldOfView;
            }

            float sensitivity = settings.MouseSensitivity;
            if (ImGui.SliderFloat("Mouse Sensitivity", ref sensitivity, 0.5f, 8f))
            {
                mouseSensitivity = sensitivity;
            }

            bool thirdPersonEnabled = settings.CameraViewMode == CameraViewMode.ThirdPerson;
            if (ImGui.Checkbox("Third Person View", ref thirdPersonEnabled))
            {
                cameraViewMode = thirdPersonEnabled
                    ? CameraViewMode.ThirdPerson
                    : CameraViewMode.FirstPerson;
            }

            float currentNoclipSpeed = settings.NoclipSpeed;
            if (ImGui.SliderFloat("Fly Speed", ref currentNoclipSpeed, GameSettings.MinNoclipSpeed, GameSettings.MaxNoclipSpeed))
            {
                noclipSpeed = currentNoclipSpeed;
            }

            int currentStrength = status.Strength;
            if (ImGui.SliderInt("Strength", ref currentStrength, PlayerCombatStats.MinStrength, PlayerCombatStats.MaxStrength))
            {
                strength = currentStrength;
            }

            float currentAttackSpeed = status.AttackSpeed;
            if (ImGui.SliderFloat("Attack Speed", ref currentAttackSpeed, PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MaxAttackSpeed))
            {
                attackSpeed = currentAttackSpeed;
            }

            int currentVitality = status.Vitality;
            if (ImGui.SliderInt("Vitality", ref currentVitality, PlayerCombatStats.MinVitality, PlayerCombatStats.MaxVitality))
            {
                vitality = currentVitality;
            }

            FloatingCombatTextTuning textTuning = settings.FloatingCombatText;
            float damageTextSize = textTuning.SizeMultiplier;
            float damageTextSpeed = textTuning.MotionSpeedMultiplier;
            float criticalDepth = textTuning.CriticalDepthMultiplier;
            if (ImGui.CollapsingHeader("Floating Damage Text", ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool changed = false;
                changed |= ImGui.SliderFloat(
                    "Damage Text Size",
                    ref damageTextSize,
                    FloatingCombatTextTuning.MinSizeMultiplier,
                    FloatingCombatTextTuning.MaxSizeMultiplier);
                changed |= ImGui.SliderFloat(
                    "Damage Text Speed",
                    ref damageTextSpeed,
                    FloatingCombatTextTuning.MinMotionSpeedMultiplier,
                    FloatingCombatTextTuning.MaxMotionSpeedMultiplier);
                changed |= ImGui.SliderFloat(
                    "Crit Depth",
                    ref criticalDepth,
                    FloatingCombatTextTuning.MinCriticalDepthMultiplier,
                    FloatingCombatTextTuning.MaxCriticalDepthMultiplier);

                if (changed)
                {
                    floatingCombatText = new FloatingCombatTextTuning(
                        damageTextSize,
                        damageTextSpeed,
                        criticalDepth);
                }
            }

            DrawViewModelControls(settings);

            bool showCollision = settings.ShowCollisionGeometry;
            if (ImGui.Checkbox("Show Collision Geometry", ref showCollision))
            {
                showCollisionGeometry = showCollision;
            }

            int floorSize = settings.PendingFloorSize;
            if (ImGui.SliderInt("Floor Size", ref floorSize, GameSettings.MinFloorSize, GameSettings.MaxFloorSize))
            {
                stagedFloorSize = floorSize;
            }
            if (ImGui.Button("Apply Floor Size"))
            {
                requestedFloorSize = floorSize;
                stagedFloorSize = floorSize;
            }

            ImGui.Text("Resolution Presets");
            if (ImGui.Button("1280 x 720"))
            {
                requestedResolution = new Vector2i(1280, 720);
            }
            ImGui.SameLine();
            if (ImGui.Button("1600 x 900"))
            {
                requestedResolution = new Vector2i(1600, 900);
            }
            ImGui.SameLine();
            if (ImGui.Button("1920 x 1080"))
            {
                requestedResolution = new Vector2i(1920, 1080);
            }

            ImGui.Text($"Current: {settings.WindowWidth} x {settings.WindowHeight}");
            ImGui.End();
        }

        return new HudInteractionRequest(
            fieldOfViewDegrees,
            mouseSensitivity,
            cameraViewMode,
            noclipSpeed,
            requestedResolution,
            stagedFloorSize,
            requestedFloorSize,
            showCollisionGeometry,
            strength,
            attackSpeed,
            vitality,
            floatingCombatText);
    }

    private static void DrawViewModelControls(GameSettings settings)
    {
        if (!ImGui.CollapsingHeader("Viewmodel", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        ViewModelRigSettings rig = settings.ViewModelRig;

        float viewModelFov = rig.FieldOfViewDegrees;
        if (ImGui.SliderFloat("Viewmodel FOV", ref viewModelFov, 35f, 100f))
        {
            rig.FieldOfViewDegrees = viewModelFov;
        }

        DrawVector3Sliders("Shared Offset", rig.PositionOffset, -1.5f, 1.5f, value => rig.PositionOffset = value);
        DrawVector3Sliders("Shared Rotation", rig.RotationOffsetDegrees, -45f, 45f, value => rig.RotationOffsetDegrees = value);

        ImGui.SeparatorText("Body Anchor");

        float spread = rig.BodyAnchor.HorizontalSpread;
        if (ImGui.SliderFloat("Body Anchor Spread", ref spread, 0f, 1.25f))
        {
            rig.BodyAnchor.HorizontalSpread = spread;
        }

        float drop = rig.BodyAnchor.VerticalDrop;
        if (ImGui.SliderFloat("Body Anchor Drop", ref drop, 0f, 1f))
        {
            rig.BodyAnchor.VerticalDrop = drop;
        }

        float forwardPull = rig.BodyAnchor.ForwardPull;
        if (ImGui.SliderFloat("Body Anchor Forward Pull", ref forwardPull, 0f, 1f))
        {
            rig.BodyAnchor.ForwardPull = forwardPull;
        }

        DrawSlotTuningControls("Hands", rig.Hands);
        DrawSlotTuningControls("Held Item", rig.HeldItem);

        if (ImGui.Button("Reset Viewmodel Tuning"))
        {
            settings.ResetViewModelRig();
        }
    }

    private static void DrawSlotTuningControls(string label, ViewModelSlotTuning tuning)
    {
        ImGui.PushID(label);
        if (ImGui.TreeNode(label))
        {
            DrawVector3Sliders("Offset", tuning.PositionOffset, -1.5f, 1.5f, value => tuning.PositionOffset = value);
            DrawVector3Sliders("Rotation", tuning.RotationOffsetDegrees, -45f, 45f, value => tuning.RotationOffsetDegrees = value);
            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    private static void DrawVector3Sliders(string label, Vector3 currentValue, float min, float max, Action<Vector3> apply)
    {
        float x = currentValue.X;
        float y = currentValue.Y;
        float z = currentValue.Z;
        bool changed = false;

        ImGui.PushID(label);
        changed |= ImGui.SliderFloat("X", ref x, min, max);
        changed |= ImGui.SliderFloat("Y", ref y, min, max);
        changed |= ImGui.SliderFloat("Z", ref z, min, max);
        ImGui.PopID();

        if (changed)
        {
            apply(new Vector3(x, y, z));
        }
    }

    public void DrawAnimatedCubeBatch(Camera camera, AnimatedModel animation, string shaderName, IReadOnlyList<Cube> cubes)
    {
        if (cubes.Count == 0)
        {
            return;
        }

        Shader shader = GetShader(shaderName);
        Matrix4 view = camera.GetViewMatrix();
        Matrix4[] modelMatrices = new Matrix4[cubes.Count];
        for (int i = 0; i < cubes.Count; i++)
        {
            Cube cube = cubes[i];
            float assetScale = ResolveCubeAssetScale(cube.Animation);
            modelMatrices[i] = CubeTransforms.CreateCenteredVisualModelMatrix(cube.Position, cube.Side, assetScale);
        }

        animation.BeginRender(shader, view, _projection);
        animation.RenderPreparedInstanced(shader, modelMatrices);
        animation.EndRender();
    }

    private static float ResolveCubeAssetScale(AnimatedModel? animation)
    {
        if (animation == null)
        {
            return 1f;
        }

        if (ReferenceEquals(animation, ModelAssets.GrassCube))
        {
            return GrassCubeAssetDefinition.VisualScale;
        }

        if (ReferenceEquals(animation, ModelAssets.DirtCube))
        {
            return DirtCubeAssetDefinition.VisualScale;
        }

        if (ReferenceEquals(animation, ModelAssets.StoneCube))
        {
            return StoneCubeAssetDefinition.VisualScale;
        }

        return 1f;
    }

}
