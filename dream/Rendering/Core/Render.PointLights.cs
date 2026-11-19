using System.Globalization;
using Dream.Rendering.Lighting;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

// Point lights (wave T2a). The scene fills Lights every frame before DrawWorld
// (PlayScene.CollectFrameLights); UploadFrameLighting then selects the nearest LightList.MaxLights
// lights in the frustum and uploads them with the frame lighting.
public partial class Render
{
    // A deterministic ring of synthetic lights around the camera, for the perf harness. Client-only,
    // default 0, clamped to 0..MaxDebugPointLights. Read once when Render is created.
    public const string DebugPointLightsEnvironmentVariable = "DREAM_DEBUG_POINT_LIGHTS";
    public const int MaxDebugPointLights = 64;

    // Ring layout: lights on a horizontal spiral around the camera, a little below eye height. Light i sits at
    // DebugRingDistance + i * DebugRingStep from it, so the lights have distinct ranks: with more than
    // LightList.MaxLights in view the selection has a real cut-off, and the kept lights keep their weight except
    // those within CutoffFadeBand of it. (On a flat circle they would all tie at the cut-off and fade to black.)
    public const float DebugRingDistance = 6f;
    public const float DebugRingStep = 0.5f;
    public const float DebugRingHeight = -1f;
    public const float DebugLightRadius = 8f;

    private static readonly Vector3[] DebugRingColors =
    [
        new(1.6f, 0.99f, 0.51f),
        new(0.5f, 0.8f, 1.6f),
        new(1.2f, 1.2f, 1.2f),
        new(0.6f, 1.5f, 0.6f),
    ];

    private PointLight[] _debugPointLights = [];

    // The frame's selected world-space lights, kept for diagnostics and tests of the wiring.
    private LightList _selectedLights;
    // The CarrierActorId of each selected light, by list index (see LightSelection.SelectNearest).
    private readonly int[] _selectedLightCarriers = new int[LightList.MaxLights];
    private readonly float[] _selectedLightFlicker = new float[LightList.MaxLights];
    private readonly Vector3[] _selectedLightWander = new Vector3[LightList.MaxLights];

    public LightCollector Lights { get; } = new();

    // The authored braziers of the current static world. PlayScene.CollectPlacedLights fills Lights from
    // it; its Placements list is what the props follow (read-only, rescanned on static content change).
    public PlacedLightIndex PlacedLights { get; } = new();

    // How many lights the last UploadFrameLighting selected (0..LightList.MaxLights).
    public int SelectedPointLightCount => _selectedLights.Count;

    public int DebugPointLightCount => _debugPointLights.Length;

    public static int ParseDebugPointLightCount(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            ? Math.Clamp(count, 0, MaxDebugPointLights)
            : 0;

    // Writes the ring around `camera` into `ring` (one light per element). Deterministic: the same
    // camera position always gives the same lights. SelectPointLights passes the position the selection ranks
    // from: FogOrigin (the isometric camera focus) when the scene sets it, else the camera eye.
    public static void FillDebugPointLightRing(Span<PointLight> ring, Vector3 camera)
    {
        for (int i = 0; i < ring.Length; i++)
        {
            float angle = i * MathF.Tau / ring.Length;
            float distance = DebugRingDistance + i * DebugRingStep;
            Vector3 position = camera + new Vector3(
                MathF.Cos(angle) * distance,
                DebugRingHeight,
                MathF.Sin(angle) * distance);
            ring[i] = new PointLight(position, DebugLightRadius, DebugRingColors[i % DebugRingColors.Length]);
        }
    }

    private void InitializeDebugPointLights()
    {
        int count = ParseDebugPointLightCount(Environment.GetEnvironmentVariable(DebugPointLightsEnvironmentVariable));
        _debugPointLights = count == 0 ? [] : new PointLight[count];
    }

    // Selects this frame's lights (scene lights plus the debug ring) for `camera`.
    private void SelectPointLights(Vector3 cameraPosition, in Matrix4 view)
    {
        if (_debugPointLights.Length > 0)
        {
            FillDebugPointLightRing(_debugPointLights, cameraPosition);
        }

        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(view, _projection);
        LightSelection.SelectNearestFaded(Lights.Lights, _debugPointLights, cameraPosition, in frustum, ref _selectedLights, _selectedLightCarriers, _selectedLightFlicker, _selectedLightWander);
    }
}
