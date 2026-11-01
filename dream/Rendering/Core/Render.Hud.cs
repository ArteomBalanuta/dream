using Dream.Application;
using Dream.Assets;
using Dream.Client.Presentation;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Actors;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
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

// Scene data the classic HUD shows that DrawHUD's arguments do not carry. The scene sets it every
// frame before the HUD pass, like WorldTransferHud (a plain struct, so nothing per frame grows).
public readonly record struct HudSceneState(Vector3 PlayerPosition);

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

    public HudSceneState HudScene { get; set; }

    private HudSurfaceSelector? _hudSelector;
    private ClassicHudSurface? _classicSurface;
    private bool _classicBackendCreated;
    private bool _classicCursorActive;
    private bool _classicSurfaceActive;
    private readonly List<FloatingCombatTextRenderState> _classicCombatText = new();
    private readonly List<Vector2> _classicCombatTextScreen = new();
    private readonly List<HudWorldLabel> _classicLabels = new();

    // True while the classic HUD drew with the sprite cursor last frame (inventory open, Settings
    // closed). The scene hides the OS cursor then.
    public bool ClassicHudCursorActive => _classicCursorActive;

    public HudSurface CurrentHudSurface(GameSettings settings) => HudSelector(settings).Current;

    // F3. A no-op unless the admin setting is available; also drops any drag in progress.
    public bool ToggleAdminHud(GameSettings settings)
    {
        bool changed = HudSelector(settings).ToggleAdmin();
        if (changed)
        {
            _classicSurface?.Input.CancelDrag();
        }

        return changed;
    }

    private HudSurfaceSelector HudSelector(GameSettings settings) =>
        _hudSelector ??= new HudSurfaceSelector(settings.AdminHudAvailable, settings.ForceImGuiHud);

    // The one place the foundation workstream's GL layer plugs in: return its IClassicHudBackend
    // (atlas store + UiBatch + submit). Called once, on the render thread, with the GL context
    // current. Null means no backend is connected, and the classic surface latches ImGui.
    private static IClassicHudBackend? CreateClassicHudBackend() => null;

    // Releases the classic backend's GL objects. Render.Lifetime's Dispose must run this.
    private void DisposeClassicHud()
    {
        _classicSurface?.Dispose();
        _classicSurface = null;
    }

    private void LatchClassicHud(HudSurfaceSelector selector, string reason, Exception? fault = null)
    {
        if (selector.Latch(reason))
        {
            _classicCursorActive = false;
            // One fault-log line, once; the ImGui surface takes over for the rest of the run.
            RenderFaultLog.Shared.Report("hud-classic", fault ?? new InvalidOperationException(reason));
        }
    }

    // Creates the backend on first use and latches ImGui on a failed or stuck load.
    private void ObserveClassicHud(HudSurfaceSelector selector, float deltaSeconds)
    {
        if (selector.Latched || selector.ForcedImGui)
        {
            return;
        }

        if (!_classicBackendCreated)
        {
            _classicBackendCreated = true;
            try
            {
                IClassicHudBackend? backend = CreateClassicHudBackend();
                if (backend is null)
                {
                    LatchClassicHud(selector, "no classic HUD backend is connected");
                    return;
                }

                _classicSurface = new ClassicHudSurface(backend);
            }
            catch (Exception ex)
            {
                LatchClassicHud(selector, ex.Message, ex);
                return;
            }
        }

        ClassicHudSurface? surface = _classicSurface;
        if (surface is null)
        {
            return;
        }

        try
        {
            if (selector.Observe(surface.State, surface.Backend.FailureReason, deltaSeconds))
            {
                _classicCursorActive = false;
                RenderFaultLog.Shared.Report("hud-classic", new InvalidOperationException(selector.LatchReason ?? "classic HUD failed"));
            }
        }
        catch (Exception ex)
        {
            LatchClassicHud(selector, ex.Message, ex);
        }
    }

    // Projects the combat text and actor labels, hands the frame to the classic surface and reads
    // back what the player picked. False while the atlas is still loading.
    private bool DrawClassicHud(
        bool windowFocused,
        GameSettings settings,
        MouseState mouseState,
        Camera camera,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        IReadOnlyList<EnemyActor> activeEnemies,
        CombatFeedbackSystem combatFeedback,
        string? rejectionMessage,
        HudStatusSnapshot status,
        PlayerInventorySnapshot? inventory,
        IEnemyRenderPoseSource? enemyPoseSource,
        double enemyRenderTick,
        out InventoryHudAction? action,
        out bool closeInventory)
    {
        action = null;
        closeInventory = false;
        ClassicHudSurface? surface = _classicSurface;
        if (surface is null || surface.State != ClassicHudBackendState.Ready)
        {
            return false;
        }

        ProjectClassicCombatText(camera, combatFeedback, settings.FloatingCombatText);
        ProjectClassicLabels(camera, remotePlayers, activeEnemies, enemyPoseSource, enemyRenderTick);

        // One coordinate source for the pointer: the same window position ImGui gets.
        bool interactive = windowFocused
            && settings.InventoryVisible
            && !settings.SettingsVisible
            && !ImGui.GetIO().WantCaptureMouse;
        UiPointer pointer = new(
            new Vector2(mouseState.Position.X, mouseState.Position.Y),
            mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left),
            mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right),
            interactive);
        Vector2i size = GetSafeViewportSize(_currentSize);
        ClassicHudFrame frame = new()
        {
            Width = size.X,
            Height = size.Y,
            ConfiguredScale = settings.UiScaleSetting,
            Pointer = pointer,
            Status = status,
            Inventory = inventory,
            InventoryOpen = settings.InventoryVisible,
            PlayerPosition = HudScene.PlayerPosition,
            RejectionMessage = rejectionMessage,
            WorldTransfer = WorldTransferHud,
            Loot = LootFeed,
            CombatText = _classicCombatText,
            CombatTextScreen = _classicCombatTextScreen,
            Labels = _classicLabels
        };
        bool drew = surface.Draw(in frame);
        if (drew)
        {
            surface.TakeRequests(out action, out closeInventory);
        }

        return drew;
    }

    private void ProjectClassicCombatText(Camera camera, CombatFeedbackSystem combatFeedback, FloatingCombatTextTuning tuning)
    {
        _classicCombatText.Clear();
        _classicCombatTextScreen.Clear();
        combatFeedback.CollectRenderStates(_floatingCombatTextStates, tuning);
        for (int i = 0; i < _floatingCombatTextStates.Count; i++)
        {
            FloatingCombatTextRenderState state = _floatingCombatTextStates[i];
            Vector3 worldPosition =
                state.WorldAnchor +
                (camera.Right * state.HorizontalOffset) +
                (camera.Up * state.VerticalOffset) -
                (camera.Front * state.DepthOffset);
            if (TryProjectWorldToScreen(camera, worldPosition, out System.Numerics.Vector2 screen))
            {
                _classicCombatText.Add(state);
                _classicCombatTextScreen.Add(new Vector2(screen.X, screen.Y));
            }
        }
    }

    private void ProjectClassicLabels(
        Camera camera,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        IReadOnlyList<EnemyActor> activeEnemies,
        IEnemyRenderPoseSource? enemyPoseSource,
        double enemyRenderTick)
    {
        _classicLabels.Clear();
        List<WorldActorLabelState> labels = _worldActorLabels;
        labels.Clear();
        ActorWorldLabelBuilder.AppendRemotePlayerLabels(remotePlayers, labels, _actorHealthLabels);
        ActorWorldLabelBuilder.AppendEnemyLabels(activeEnemies, labels, _actorHealthLabels, enemyPoseSource, enemyRenderTick);
        for (int i = 0; i < labels.Count; i++)
        {
            WorldActorLabelState label = labels[i];
            if (TryProjectWorldToScreen(camera, label.WorldAnchor, out System.Numerics.Vector2 screen))
            {
                _classicLabels.Add(new HudWorldLabel(new Vector2(screen.X, screen.Y), label.DisplayName, label.HealthLabel));
            }
        }
    }

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
        HudSurfaceSelector selector = HudSelector(settings);
        ObserveClassicHud(selector, (float)args.Time);
        BeginHudFrame(args, windowFocused, settings, mouseState);

        InventoryHudAction? classicAction = null;
        bool classicClose = false;
        if (selector.Current == HudSurface.Classic)
        {
            // CPU build, then one submit. A failure latches ImGui and this same frame falls through
            // to the ImGui surface (the build touches no GL, and the backend restores GL state).
            try
            {
                bool drew = DrawClassicHud(
                    windowFocused, settings, mouseState, camera, remotePlayers, activeEnemies,
                    combatFeedback, rejectionMessage, status, inventory, enemyPoseSource, enemyRenderTick,
                    out classicAction, out classicClose);
                _classicCursorActive = drew && settings.InventoryVisible && !settings.SettingsVisible;
            }
            catch (Exception ex)
            {
                classicAction = null;
                classicClose = false;
                LatchClassicHud(selector, ex.Message, ex);
            }
        }
        else
        {
            _classicCursorActive = false;
        }

        // Read by DrawHudInterface: with the classic surface it keeps only Settings and Build.
        _classicSurfaceActive = selector.Current == HudSurface.Classic;
        if (!_classicSurfaceActive)
        {
            DrawFloatingCombatText(camera, combatFeedback, settings.FloatingCombatText);
            DrawWorldActorLabels(camera, remotePlayers, activeEnemies, enemyPoseSource, enemyRenderTick);
        }

        HudInteractionRequest request = DrawHudInterface(
            settings,
            status,
            rejectionMessage,
            connectionLabel,
            playerRoster,
            performance,
            WorldTransferHud,
            inventory);
        if (classicAction is not null || classicClose)
        {
            request = request with
            {
                InventoryAction = classicAction ?? request.InventoryAction,
                CloseInventory = classicClose || request.CloseInventory
            };
        }

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
        // The classic inventory draws its own sprite cursor, so ImGui's stays off there.
        bool classicCursor = _classicCursorActive && settings.InventoryVisible && !settings.SettingsVisible;
        io.MouseDrawCursor = overlayVisible && !classicCursor;
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
