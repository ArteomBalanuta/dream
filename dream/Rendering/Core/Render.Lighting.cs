using Dream.Rendering.Lighting;
using Dream.Rendering.Mist;
using Dream.Simulation.Players;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

public partial class Render
{
    private FrameLighting _frameLighting = FrameLighting.ReferenceNoon;

    // DREAM_FX_CLOCK pins the clock that clouds and wind read (FrameLighting.FrameTime.x) so captures
    // are reproducible. Read once. Unset, blank or non-finite means the live clock.
    public const string FxClockEnvironmentVariable = "DREAM_FX_CLOCK";

    private static readonly double? PinnedFxClockSeconds = ParsePinnedClock(Environment.GetEnvironmentVariable(FxClockEnvironmentVariable));

    internal static double? ParsePinnedClock(string? text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
        && double.IsFinite(v) ? v : null;

    // The wrapped shader clock for this frame: the pin, else the scene's clock (BoundaryMist.TimeSeconds).
    private float FxClockSeconds() => BoundaryMistField.WrapTime(PinnedFxClockSeconds ?? BoundaryMist.TimeSeconds);

    // The FrameLighting uniform buffer. Created with the shaders, disposed with them.
    private FrameLightingBuffer? _frameLightingBuffer;

    // The LightList uniform buffer (point lights, wave T2a). Same lifetime as _frameLightingBuffer.
    private LightListBuffer? _lightListBuffer;

    // The BoundaryMist uniform buffer (shaders/boundaryMist.glsl). Same lifetime as _frameLightingBuffer.
    private BoundaryMistBuffer? _boundaryMistBuffer;

    // The map size, clock and switch of the boundary mist wall. The scene sets it each frame before
    // DrawWorld; the default (no map) uploads strength 0.
    public BoundaryMistInput BoundaryMist { get; set; }

    // Lighting the next world pass uploads. The scene sets it each frame before DrawWorld.
    public void SetFrameLighting(in FrameLighting lighting) => _frameLighting = lighting;

    public ref readonly FrameLighting CurrentFrameLighting => ref _frameLighting;

    // Uploads CurrentFrameLighting for this frame (world and view-model ranges), selects the
    // nearest point lights in the frustum from Lights (plus the debug ring) and uploads them
    // (world and view-model ranges), and binds both world ranges. WorldRenderer.DrawWorld calls it
    // once per frame, before any world draw.
    public void UploadFrameLighting(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Vector3 cameraPosition = FogOrigin ?? camera.Position;
        Matrix4 view = camera.GetViewMatrix();
        _frameLightingBuffer?.Upload(in _frameLighting, cameraPosition, in view, FxClockSeconds());
        SelectPointLights(cameraPosition, in view);
        _lightListBuffer?.Upload(in _selectedLights, in view);
        _boundaryMistBuffer?.Upload(BoundaryMist, camera.Position);
    }

    public void BindWorldLighting()
    {
        _frameLightingBuffer?.BindWorld();
        _lightListBuffer?.BindWorld();
        _boundaryMistBuffer?.BindWorld();
    }

    public void BindViewModelLighting()
    {
        _frameLightingBuffer?.BindViewModel();
        _lightListBuffer?.BindViewModel();
        _boundaryMistBuffer?.BindViewModel();
    }

    // Creates the buffers and fills both ranges of each with the current lighting (camera at the
    // origin) and no point lights, so a world program drawn before the first DrawWorld still reads
    // defined values.
    private void InitializeFrameLighting()
    {
        _frameLightingBuffer = new FrameLightingBuffer();
        _frameLightingBuffer.Upload(in _frameLighting, Vector3.Zero, Matrix4.Identity);
        _selectedLights = default;
        _lightListBuffer = new LightListBuffer();
        _lightListBuffer.Upload(in _selectedLights, Matrix4.Identity);
        _boundaryMistBuffer = new BoundaryMistBuffer();
        _boundaryMistBuffer.Upload(default, Vector3.Zero);
        InitializeDebugPointLights();
    }

    private void DisposeFrameLighting()
    {
        try
        {
            _boundaryMistBuffer?.Dispose();
            _boundaryMistBuffer = null;
        }
        finally
        {
            try
            {
                _lightListBuffer?.Dispose();
                _lightListBuffer = null;
            }
            finally
            {
                _frameLightingBuffer?.Dispose();
                _frameLightingBuffer = null;
            }
        }
    }
}
