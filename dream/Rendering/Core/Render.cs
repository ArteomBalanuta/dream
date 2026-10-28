using Dream.Application;
using Dream.Rendering.Actors;
using Dream.Rendering.Hud;
using Dream.Rendering.ViewModels;
using Dream.Rendering.World;

namespace Dream.Rendering.Core;

using OpenTK.Mathematics;

public partial class Render : IDisposable
{
    private readonly List<FloatingCombatTextRenderState> _floatingCombatTextStates = new();
    private readonly ReusableMatrixBuffer _geometryBlockMatrices = new();
    private readonly ReusableMatrixBuffer _animatedCubeMatrices = new();
    private readonly StaticTerrainTextureCache _staticTerrainTextures = new();
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
    private readonly LocalPlayerRenderer _localPlayerRenderer;
    private readonly RemotePlayerRenderer _remotePlayerRenderer;
    private readonly DebugRenderer _debugRenderer;
    private readonly HudRenderer _hudRenderer;
    private readonly ViewModelRenderer _viewModelRenderer;
    private bool _disposed;

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

    public WorldRenderer World => _worldRenderer;

    public EnemyRenderer Enemies => _enemyRenderer;

    public DebugRenderer Debug => _debugRenderer;

    public HudRenderer Hud => _hudRenderer;

    public ViewModelRenderer ViewModels => _viewModelRenderer;

    public LocalPlayerRenderer LocalPlayers => _localPlayerRenderer;

    public RemotePlayerRenderer RemotePlayers => _remotePlayerRenderer;

    public Vector2i ViewportSize => GetSafeViewportSize(_currentSize);

    public Matrix4 ProjectionMatrix => _projection;
}
