using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;

public class PlayScene : IDisposable
{
    private readonly Render _render;
    private readonly Player _player;
    private Map _map;
    private readonly PhysicsIntegrator _physics;
    private readonly PhysicsSyncSystem _physicsSync = new();
    private readonly GameSettings _settings;
    private readonly PlayerViewModelController _viewModelController;
    private bool _wasSettingsTogglePressed;

    public CursorState DesiredCursorState => _settings.CurrentRuntimeState.DesiredCursorState;

    public Vector3 LastReportedPlayerPosition => _player.LastReportedPosition;

    public PlayScene(GameSettings settings)
    {
        _settings = settings;
        _physics = new PhysicsIntegrator();
        _map = new Map(_physics, width: _settings.FloorSize, depth: _settings.FloorSize);
        _viewModelController = new PlayerViewModelController(ViewModelCatalog.CreateDefault(), _settings);

        Vector3 playerStartPosition = new Vector3(5, 1.5f, 5);
        _player = new Player(_map, _physics, _physics.PlayerBody, playerStartPosition);
        _render = new Render(_settings);
    }

    public void Update(FrameEventArgs args, KeyboardState keyboardState, MouseState mouseState)
    {
        float delta = (float)args.Time;
        _viewModelController.RefreshPose();
        HandleGlobalInput(keyboardState);
        bool allowGameplayInput = _settings.CurrentRuntimeState.AllowGameplayInput;
        bool primaryUseTriggered = _player.ConsumePrimaryUsePressed(mouseState, allowGameplayInput);
        if (primaryUseTriggered)
        {
            _viewModelController.TriggerPrimaryUse();
        }

        ViewModelUpdateResult viewModelUpdateResult =
            _render.ViewModels.Update(_viewModelController.CurrentFrameState, delta);

        if (viewModelUpdateResult.HandsFinished)
        {
            _viewModelController.NotifyAnimationFinished(ViewModelSlot.Hands);
        }

        if (viewModelUpdateResult.HeldItemFinished)
        {
            _viewModelController.NotifyAnimationFinished(ViewModelSlot.HeldItem);
        }

        _physics.StepSimulation(delta);
        _player.Update(delta, keyboardState, mouseState, _settings, allowGameplayInput);
        _player.PlaceCube(_map);
        _physicsSync.SyncDynamicMapBodies(_physics.DynamicMapCubeBodies);
    }

    public void Render(FrameEventArgs args, bool windowFocused, MouseState mouseState, Action<Vector2i> applyClientSize)
    {
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _render.World.DrawWorld(
            _player.Camera,
            _map.GetRenderCandidateCubes(_player.Camera.Position, WorldVisibility.MaxRenderDistance));

        if (_settings.ShowCollisionGeometry)
        {
            _render.Debug.DrawCollisionBodies(_physics.MapCubeBodies, _player.Camera);
            _render.DrawCollisionBody(_physics.PlayerBody, _player.Camera);
        }

        _render.ViewModels.Draw(_viewModelController.CurrentFrameState);

        HudFrameResult hudResult = _render.Hud.DrawFrame(args, windowFocused, _settings, mouseState);
        if (hudResult.RequestedResolution.HasValue)
        {
            applyClientSize(hudResult.RequestedResolution.Value);
            _settings.ApplyResolution(hudResult.RequestedResolution.Value);
        }

        _render.Hud.UpdateProjectionForSettings(_settings);

        if (hudResult.RequestedFloorSize.HasValue && hudResult.RequestedFloorSize.Value != _settings.FloorSize)
        {
            _settings.StageFloorSize(hudResult.RequestedFloorSize.Value);
            _settings.ApplyPendingFloorSize();
            RebuildMapForFloorSize(_settings.FloorSize);
        }
    }

    public void Resize(Vector2i size)
    {
        GL.Viewport(0, 0, size.X, size.Y);
        _settings.ApplyResolution(size);
        _render.Hud.UpdateProjectionForSettings(_settings);
    }

    public void Dispose()
    {
        _physics.Dispose();
    }

    private void HandleGlobalInput(KeyboardState keyboardState)
    {
        bool settingsTogglePressed = keyboardState.IsKeyDown(Keys.Escape);
        if (settingsTogglePressed && !_wasSettingsTogglePressed)
        {
            _settings.ToggleSettings();
        }

        _wasSettingsTogglePressed = settingsTogglePressed;
    }

    private void RebuildMapForFloorSize(int floorSize)
    {
        Vector3 currentPlayerPosition = _player.GetWorldPosition();
        Map previousMap = _map;

        _physics.ResetMapBodies();

        var rebuiltMap = new Map(_physics, width: floorSize, depth: floorSize);
        previousMap.CopyPlacedCubesTo(rebuiltMap);

        _map = rebuiltMap;
        _player.map = rebuiltMap;

        Vector3 clampedPosition = new Vector3(
            Math.Clamp(currentPlayerPosition.X, 0.5f, rebuiltMap.Width - 0.5f),
            Math.Clamp(currentPlayerPosition.Y, 1.5f, rebuiltMap.Height - 0.5f),
            Math.Clamp(currentPlayerPosition.Z, 0.5f, rebuiltMap.Depth - 0.5f));

        _player.SetWorldPosition(clampedPosition);
    }
}
