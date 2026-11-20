using Dream.Application;
using Dream.Assets;
using Dream.Client.Presentation;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Actors;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
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

// The pointer as click-to-move sees it. OverHud is true when a press at this moment would land on
// the HUD: a window, the bar, an open modal, a drag in progress, or Settings.
public readonly record struct WorldPointerState(Vector2 Position, bool LeftDown, bool RightDown, bool OverHud);

public partial class Render
{
    // The quickbar's state, shared by the scene every frame before the HUD pass (M4); null draws none.
    public QuickbarHudState? Quickbar { get; set; }

    // Set by the scene every update; carried into the classic HUD frame.
    public bool PointerOverEnemy { get; set; }

    // The world hover for the classic HUD, set by the scene: the kind under the pointer (cursor), and the info
    // panel's state and content (HoverInfo null: no panel).
    public HoverKind HoverCursorKind { get; set; }

    public HoverPanelState HoverPanel { get; set; }

    public HoverInfo? HoverInfo { get; set; }

    // The clickable world label the classic HUD's pointer rested on last frame, or default.
    // Only from a frame the classic HUD really built: after a fault (latched to ImGui), a skipped frame or a
    // switch to the ImGui surface the last label hover would otherwise stay pinned as the world hover target.
    public HudLabelClick ClassicHoveredLabel => _classicLabelsBuilt ? _classicSurface?.HoveredLabel ?? default : default;

    private bool _classicLabelsBuilt;

    // Set by the scene every update while a camera rotation drag is held: the cursor shows Grab.
    public bool CameraDragging { get; set; }

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
    private const float MaxLoadingStepSeconds = 0.1f;
    private readonly List<FloatingCombatTextRenderState> _classicCombatText = new();
    private readonly List<HudWorldLabel> _classicLabels = new();

    // True while the classic HUD draws (or is about to draw) the sprite cursor: Settings closed.
    // The scene hides the OS cursor then. Read by Update before the frame is drawn,
    // so it is predicted from the surface state instead of waiting for the last draw; otherwise the
    // OS cursor and the sprite cursor would both show for the first frame after V.
    public bool ClassicHudCursorActive => _classicCursorActive || ClassicHudWillDraw();

    // Test-only pointer override (DREAM_HUD_CAPTURE_SCRIPT). Null in normal play, which keeps the
    // real mouse the only source.
    internal UiScriptedPointer? ScriptedPointer { get; set; }

    // The classic surface and its view, for the capture script's logs and rect lookups.
    internal ClassicHudSurface? ClassicSurfaceForCapture => _classicSurface;

    // The pointer for click-to-move, read in Update. The scripted pointer (capture script) takes the
    // same path as a hand. The hover state is the previous Render's: ImGui's capture flag and the
    // classic router's hot id are one frame old, and the bar and window rects are hit-tested live.
    public WorldPointerState SampleWorldPointer(MouseState mouseState, GameSettings settings, bool windowFocused)
    {
        Vector2 position;
        bool left;
        bool right;
        if (ScriptedPointer is { Active: true } scripted)
        {
            position = scripted.Position;
            left = scripted.LeftDown;
            right = scripted.RightDown;
        }
        else
        {
            position = new Vector2(mouseState.Position.X, mouseState.Position.Y);
            left = windowFocused && mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left);
            right = windowFocused && mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right);
        }

        bool overHud = settings.SettingsVisible || ImGui.GetIO().WantCaptureMouse;
        if (!overHud && ClassicHudWillDraw())
        {
            UiInputRouter? router = _classicSurface?.Input;
            overHud = router is not null
                && (router.HotId != UiInputRouter.NoId || router.Dragging || router.ModalActive);
            if (!overHud)
            {
                Vector2i size = GetSafeViewportSize(_currentSize);
                UiLayout layout = UiLayout.Create(size.X, size.Y, settings.UiScaleSetting);
                overHud = ClassicHudHitTest.Over(in layout, settings.InventoryVisible, position);
            }
        }

        return new WorldPointerState(position, left, right, overHud);
    }

    private bool ClassicHudWillDraw() =>
        _hudSelector is not null && ClassicCursorPolicy.WillDraw(_hudSelector.Current, _classicSurface?.State);

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

    // Called once, on the render thread, with the GL context current.
    private IClassicHudBackend CreateClassicHudBackend() => new GlClassicHudBackend(this);

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
                _classicSurface = new ClassicHudSurface(CreateClassicHudBackend());
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
            // Starts the atlas on the first call, then uploads at most one page per frame.
            BeginHudFrame();
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

        ProjectClassicCombatText(camera, combatFeedback, settings.FloatingCombatText, settings.CameraViewMode);
        ProjectClassicLabels(camera, remotePlayers, activeEnemies, enemyPoseSource, enemyRenderTick);

        // One coordinate source for the pointer: the same window position ImGui gets, in the same
        // pixels the layout uses (the window's client size and the framebuffer are one size here).
        UiPointer pointer;
        if (ScriptedPointer is { Active: true } scripted)
        {
            // Capture script only: synthetic states through the same router. Focus does not apply.
            pointer = new UiPointer(
                scripted.Position,
                scripted.LeftDown,
                scripted.RightDown,
                !settings.SettingsVisible);
        }
        else
        {
            bool interactive = windowFocused
                && !settings.SettingsVisible
                && !ImGui.GetIO().WantCaptureMouse;
            pointer = new UiPointer(
                new Vector2(mouseState.Position.X, mouseState.Position.Y),
                mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left),
                mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right),
                interactive);
        }

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
            Labels = _classicLabels,
            Vendor = Vendor,
            Quickbar = Quickbar,
            PointerOverEnemy = PointerOverEnemy,
            HoverCursorKind = HoverCursorKind,
            HoverPanel = HoverPanel,
            HoverInfo = HoverInfo,
            CameraDragging = CameraDragging
        };
        HudSurfaceSelector selector = HudSelector(settings);
        bool drew = ClassicHudFrameGuard.Draw(surface, in frame, out action, out closeInventory, out Exception? fault);
        if (fault is not null)
        {
            LatchClassicHud(selector, fault.Message, fault);
        }

        return drew;
    }

    private void ProjectClassicCombatText(Camera camera, CombatFeedbackSystem combatFeedback, FloatingCombatTextTuning tuning, CameraViewMode viewMode)
    {
        _classicCombatText.Clear();
        combatFeedback.CollectRenderStates(_floatingCombatTextStates, tuning);
        for (int i = 0; i < _floatingCombatTextStates.Count; i++)
        {
            // The anchor alone: the sprite panel adds its own rise and jitter in pixels, so the ImGui
            // path offsets stay out. A state that cannot be projected is dropped.
            FloatingCombatTextRenderState state = _floatingCombatTextStates[i];
            bool projected = TryProjectWorldToScreen(camera, state.WorldAnchor, out System.Numerics.Vector2 screen);
            if (IncomingNumberAnchor.UsesScreenAnchor(state.Kind, viewMode, projected))
            {
                // The local player's own number in first person: its anchor is at the camera.
                Vector2i viewport = GetSafeViewportSize(_currentSize);
                _classicCombatText.Add(state with { ScreenAnchor = IncomingNumberAnchor.ScreenAnchor(viewport.X, viewport.Y) });
            }
            else if (projected)
            {
                _classicCombatText.Add(state with { ScreenAnchor = new Vector2(screen.X, screen.Y) });
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
        // The actor the hover info panel shows loses its floating label while the panel is up.
        HoverTargetId panel = HoverInfoPanel.WillDraw(HoverPanel, HoverInfo) ? HoverInfo!.Target : HoverTargetId.None;
        int? skipPlayer = panel.Kind == HoverKind.RemotePlayer ? (int)panel.Id : null;
        int? skipEnemy = panel.Kind == HoverKind.Enemy ? (int)panel.Id : null;
        ActorWorldLabelBuilder.AppendRemotePlayerLabels(remotePlayers, labels, _actorHealthLabels, skipPlayer);
        ActorWorldLabelBuilder.AppendEnemyLabels(activeEnemies, labels, _actorHealthLabels, enemyPoseSource, enemyRenderTick, skipEnemy);
        for (int i = 0; i < labels.Count; i++)
        {
            WorldActorLabelState label = labels[i];
            if (TryProjectWorldToScreen(camera, label.WorldAnchor, out System.Numerics.Vector2 screen))
            {
                _classicLabels.Add(new HudWorldLabel(new Vector2(screen.X, screen.Y), label.DisplayName, label.HealthLabel));
            }
        }

        AppendEconomyLabels(camera);
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
        // Capped so one long frame (a world load) cannot use up the loading timeout by itself.
        ObserveClassicHud(selector, MathF.Min((float)args.Time, MaxLoadingStepSeconds));
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
                _classicCursorActive = drew && !settings.SettingsVisible;
                _classicLabelsBuilt = drew;
            }
            catch (Exception ex)
            {
                _classicLabelsBuilt = false;
                classicAction = null;
                classicClose = false;
                LatchClassicHud(selector, ex.Message, ex);
            }
        }
        else
        {
            _classicCursorActive = false;
            _classicLabelsBuilt = false;
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

        if (_classicSurfaceActive)
        {
            request = MergeEconomyRequest(request);
            if (_classicSurface?.TakeQuickbarCycle() is int quickbarSlot and >= 0)
            {
                request = request with { QuickbarCycleSlot = quickbarSlot };
            }
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
        // The cursor picks the target in the isometric view, so there is no aim point to mark.
        if (settings.CameraViewMode == CameraViewMode.Isometric
            || settings.SettingsVisible || settings.InventoryVisible || _currentSize.X <= 0 || _currentSize.Y <= 0)
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
        // ImGui always gets the real mouse, so the Build panel works during play. The classic HUD
        // draws its own sprite cursor, so the ImGui one stays off there, except over an ImGui window
        // (the Build panel), where the sprite cursor is not drawn and the OS one is hidden.
        io.MouseDrawCursor = ClassicCursorPolicy.ImGuiDrawsCursor(
            ClassicHudCursorActive, settings.SettingsVisible, settings.PauseMenuVisible, io.WantCaptureMouse);
        io.MousePos = windowFocused
            ? new System.Numerics.Vector2(mouseState.Position.X, mouseState.Position.Y)
            : new System.Numerics.Vector2(-float.MaxValue, -float.MaxValue);
        io.MouseDown[0] = windowFocused && mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left);
        io.MouseDown[1] = windowFocused && mouseState.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right);

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
