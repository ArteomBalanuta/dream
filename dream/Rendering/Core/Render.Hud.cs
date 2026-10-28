using Dream.Application;
using Dream.Assets;
using Dream.Client.Presentation;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Actors;
using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World.Geometry;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ImGuiNET;
using System.Diagnostics.CodeAnalysis;

namespace Dream.Rendering.Core;

public readonly record struct HudFrameResult(
    HudInteractionRequest Request);

public partial class Render
{
    [MemberNotNull(nameof(_hudShader), nameof(_shaderObj))]
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

    private bool _wasFocused = true;

    // The world transfer in progress, for the HUD's "receiving world" line. The scene sets it
    // every frame before the HUD pass (a plain struct field, so nothing per frame grows the
    // presentation or performance snapshots); WorldTransferHudState.None hides the line.
    public WorldTransferHudState WorldTransferHud { get; set; }

    // The building menu's current pick (null is Auto), shown by the "Build" window. The scene sets
    // it when it sends a new selection (PlayScene.HudRequests.cs), not per frame.
    public GeometryMaterialId? BuildMaterialSelection { get; set; }

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
        PlayerInventorySnapshot? inventory = null,
        string? connectionLabel = null,
        FramePerformanceSnapshot performance = default,
        IEnemyRenderPoseSource? enemyPoseSource = null,
        double enemyRenderTick = 0d)
    {
        GL.Disable(EnableCap.DepthTest);
        DrawCrosshair(settings);
        BeginHudFrame(args, windowFocused, settings, mouseState);
        DrawFloatingCombatText(camera, combatFeedback, settings.FloatingCombatText);
        DrawWorldActorLabels(camera, remotePlayers, activeEnemies, enemyPoseSource, enemyRenderTick);
        HudInteractionRequest request = DrawHudInterface(
            settings,
            status,
            rejectionMessage,
            connectionLabel,
            playerRoster,
            performance,
            WorldTransferHud,
            inventory);
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
        IReadOnlyList<EnemyActor> activeEnemies,
        IEnemyRenderPoseSource? enemyPoseSource,
        double enemyRenderTick)
    {
        List<WorldActorLabelState> labels = _worldActorLabels;
        labels.Clear();
        ActorWorldLabelBuilder.AppendRemotePlayerLabels(remotePlayers, labels, _actorHealthLabels);
        ActorWorldLabelBuilder.AppendEnemyLabels(activeEnemies, labels, _actorHealthLabels, enemyPoseSource, enemyRenderTick);
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
        if ((settings.SettingsVisible || settings.InventoryVisible) || _currentSize.X <= 0 || _currentSize.Y <= 0)
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
        RenderCounters.CountDraw();
        GL.BindVertexArray(0);
    }

    private void BeginHudFrame(FrameEventArgs args, bool windowFocused, GameSettings settings, MouseState mouseState)
    {
        var io = ImGui.GetIO();
        Vector2i safeViewportSize = GetSafeViewportSize(_currentSize);

        io.DisplaySize = new System.Numerics.Vector2(safeViewportSize.X, safeViewportSize.Y);
        io.DeltaTime = (float)args.Time;
        bool overlayVisible = settings.SettingsVisible || settings.InventoryVisible;
        io.MouseDrawCursor = overlayVisible;
        io.MousePos = overlayVisible
            ? new System.Numerics.Vector2(mouseState.Position.X, mouseState.Position.Y)
            : new System.Numerics.Vector2(-float.MaxValue, -float.MaxValue);
        io.MouseDown[0] = overlayVisible && mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left);
        io.MouseDown[1] = overlayVisible && mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right);

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
}
