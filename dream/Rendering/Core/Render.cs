using Dream.Application;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Rendering.Hud;
using Dream.Rendering.ViewModels;
using Dream.Rendering.World;

namespace Dream.Rendering.Core;

using OpenTK.Mathematics;

public partial class Render : IDisposable
{
    private readonly List<FloatingCombatTextRenderState> _floatingCombatTextStates = new();
    private readonly StaticTerrainTextureCache _staticTerrainTextures = new();
    private readonly StaticTerrainTileArray _terrainTiles = new();
    private readonly List<WorldActorLabelState> _worldActorLabels = new();
    private readonly ActorHealthLabelCache _actorHealthLabels = new();

    private int _vertexArrayObject;
    private int _vertexBufferObject;
    private readonly StaticMeshBuffer _fallbackCubeBuffer;
    private readonly DisposableRegistry<string, Shader> _shaders = new();
    private Matrix4 _projection;

    // Cube vertex data: note that you might want to use indexed drawing.
    private static readonly float[] CubeVertices = {
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

    // One four-vertex triangle fan per face, built once instead of on every draw.
    private static readonly (int First, int Count)[] CubeFaceDrawRanges = CreateCubeFaceDrawRanges(CubeVertices.Length / 3);

    private int _hudVao;
    private int _hudVbo;
    private Shader _hudShader;

    private IntPtr _imGuiContext;
    private int _fontTexture;
    private Shader _shaderObj;
    private int _shader;
    private int _projMtxLocation;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _indexBuffer;
    private Vector2i _currentSize;
    private Vector2i _crosshairViewportSize;
    private readonly WorldRenderer _worldRenderer;
    private readonly EnemyRenderer _enemyRenderer;
    private readonly Dream.Rendering.Props.PropRenderer _propRenderer;
    private readonly LocalPlayerRenderer _localPlayerRenderer;
    private readonly RemotePlayerRenderer _remotePlayerRenderer;
    private readonly DebugRenderer _debugRenderer;
    private readonly HudRenderer _hudRenderer;
    private readonly ViewModelRenderer _viewModelRenderer;
    private bool _disposed;
    private float _isometricDistance = IsometricView.Distance;
    private bool _projectionIsometric;
    private float _projectionFieldOfViewDegrees;

    private static Vector2i GetSafeViewportSize(Vector2i size)
    {
        return new Vector2i(Math.Max(size.X, 1), Math.Max(size.Y, 1));
    }

    private void UpdateProjection(int width, int height, float fieldOfViewDegrees, bool isometric)
    {
        _currentSize = new Vector2i(width, height);
        _projectionIsometric = isometric;
        _projectionFieldOfViewDegrees = fieldOfViewDegrees;
        Vector2i safeViewportSize = GetSafeViewportSize(_currentSize);
        float aspectRatio = (float)safeViewportSize.X / safeViewportSize.Y;

        _projection = isometric
            ? IsometricView.CreateProjection(aspectRatio, _isometricDistance)
            : Matrix4.CreatePerspectiveFieldOfView(
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
        UpdateProjection(settings.WindowWidth, settings.WindowHeight, settings.FieldOfViewDegrees, settings.CameraViewMode == CameraViewMode.Isometric);
    }

    // The tactical camera's current distance: only the isometric far plane depends on it (IsometricView.FarPlaneFor),
    // so the projection is rebuilt only when it changes.
    public void SetIsometricDistance(float distance)
    {
        if (!float.IsFinite(distance) || distance <= 0f || distance == _isometricDistance)
        {
            return;
        }

        _isometricDistance = distance;
        if (_projectionIsometric)
        {
            UpdateProjection(_currentSize.X, _currentSize.Y, _projectionFieldOfViewDegrees, true);
        }
    }

    // Screen up on the ground for the shadow receiver box; null keeps the fixed view's direction.
    public Vector3? ShadowViewForwardGround { get; set; }

    public WorldRenderer World => _worldRenderer;

    public EnemyRenderer Enemies => _enemyRenderer;

    // Scenery: the placed props (houses, well, braziers, graves). Client-visual only; see PropLayout.
    public Dream.Rendering.Props.PropRenderer Props => _propRenderer;

    public DebugRenderer Debug => _debugRenderer;

    public HudRenderer Hud => _hudRenderer;

    public ViewModelRenderer ViewModels => _viewModelRenderer;

    public LocalPlayerRenderer LocalPlayers => _localPlayerRenderer;

    public RemotePlayerRenderer RemotePlayers => _remotePlayerRenderer;

    public Vector2i ViewportSize => GetSafeViewportSize(_currentSize);

    public Matrix4 ProjectionMatrix => _projection;

    // Set by the play scene each frame before the world draws. FogOrigin replaces the camera position
    // for fog distance (isometric: the focus point).
    public Dream.Simulation.Players.Camera? LastWorldCamera { get; set; }

    public Vector3? FogOrigin { get; set; }
}
