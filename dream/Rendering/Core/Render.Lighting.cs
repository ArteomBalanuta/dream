using Dream.Rendering.Lighting;
using Dream.Rendering.Mist;
using Dream.Simulation.Players;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

public partial class Render
{
    private FrameLighting _frameLighting = FrameLighting.ReferenceNoon;

    // The UNWRAPPED shader clock for this frame: the pin, else the scene's clock (BoundaryMist.TimeSeconds).
    // FrameLightingBuffer reduces it per consumer (sway: 20 pi s, clouds: 64 cells); the mist's own 6400 s
    // wrap (BoundaryMistField.WrapTime) is periodic for the mist noise only and must not be applied here.
    private double FxClockSeconds() => FxClockPin.Seconds ?? _boundaryMist.TimeSeconds;

    // The FrameLighting uniform buffer. Created with the shaders, disposed with them.
    private FrameLightingBuffer? _frameLightingBuffer;

    // The LightList uniform buffer (point lights, wave T2a). Same lifetime as _frameLightingBuffer.
    private LightListBuffer? _lightListBuffer;

    // The BoundaryMist uniform buffer (shaders/boundaryMist.glsl). Same lifetime as _frameLightingBuffer.
    private BoundaryMistBuffer? _boundaryMistBuffer;

    // The FireShadowList uniform buffer (shaders/fireShadows.glsl). Same lifetime as _frameLightingBuffer.
    private Dream.Rendering.Shadows.FireShadowListBuffer? _fireShadowListBuffer;

    // The map size, clock and switch of the boundary mist wall. The scene sets it each frame before
    // DrawWorld; the default (no map) uploads strength 0.
    // Under DREAM_FX_CLOCK the setter swaps in the pinned clock (FxClockPin), so the animated void mist holds still.
    public BoundaryMistInput BoundaryMist
    {
        get => _boundaryMist;
        set => _boundaryMist = FxClockPin.Apply(value);
    }

    private BoundaryMistInput _boundaryMist;

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
        // The fire half of the shadow frame PrepareShadows just filled and rendered the atlas for. It has to be uploaded
        // after that pass (the slots and their flickered positions are only known then) and before any world draw samples
        // the atlas, and this is the one point in the frame between the two. Upload binds the world range itself.
        _fireShadowListBuffer?.Upload(in _shadowFrame);
    }

    public void BindWorldLighting()
    {
        _frameLightingBuffer?.BindWorld();
        _lightListBuffer?.BindWorld();
        _boundaryMistBuffer?.BindWorld();
        _fireShadowListBuffer?.BindWorld();
    }

    public void BindViewModelLighting()
    {
        _frameLightingBuffer?.BindViewModel();
        _lightListBuffer?.BindViewModel();
        _boundaryMistBuffer?.BindViewModel();
        // Count 0 by construction: the view model's fragPos is in eye space and the atlas holds world-space depth, so
        // dreamFireVisibility must return a literal 1.0 there rather than read an arbitrary tile.
        _fireShadowListBuffer?.BindViewModel();
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
        _fireShadowListBuffer = new Dream.Rendering.Shadows.FireShadowListBuffer();
        _fireShadowListBuffer.Upload(Dream.Rendering.Shadows.ShadowFrame.None);
        InitializeDebugPointLights();
    }

    private void DisposeFrameLighting()
    {
        try
        {
            try
            {
                _fireShadowListBuffer?.Dispose();
                _fireShadowListBuffer = null;
            }
            finally
            {
                _boundaryMistBuffer?.Dispose();
                _boundaryMistBuffer = null;
            }
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

// DREAM_FX_CLOCK: pins the clock that clouds, wind (FrameLighting.FrameTime) and the boundary mist read. It does NOT
// pin the fire: the FX simulation and the torch/halo/flame/ember light collectors read the presentation clock
// directly, so a capture under the pin still has moving torch flicker, flame and embers. Read once. Unset, blank or non-finite means the live clock.
internal static class FxClockPin
{
    public const string EnvironmentVariable = "DREAM_FX_CLOCK";

    public static readonly double? Seconds = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static double? Parse(string? text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
        && double.IsFinite(v) ? v : null;

    // The boundary mist input with its clock replaced by the pin, when there is one; otherwise unchanged.
    public static BoundaryMistInput Apply(BoundaryMistInput input, double? pinnedSeconds) =>
        pinnedSeconds is { } pin ? input with { TimeSeconds = pin } : input;

    public static BoundaryMistInput Apply(BoundaryMistInput input) => Apply(input, Seconds);
}
