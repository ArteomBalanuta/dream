using Dream.Rendering.Lighting;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.Props;

// The props of the current static world, rescanned only when the static content (or the brazier placements it
// is derived from) changes - the same discipline as PlacedLightIndex, whose Placements it consumes. An unknown
// content version (0) is rescanned every frame. Steady state allocates nothing.
public sealed class PropIndex
{
    private readonly StaticGround _ground = new();
    private readonly List<PropPlacement> _placements = [];
    private long _staticVersion;
    private int _lightRescans = -1;
    private bool _hasScan;

    // Times the layout was rebuilt (tests).
    public int RebuildCount { get; private set; }

    // The ground the last Update scanned; RebuildCount is its change signal. Shared: callers must not mutate.
    public StaticGround Ground => _ground;

    // Sorted by kind then position. Shared and reused: callers must not mutate.
    public IReadOnlyList<PropPlacement> Placements => _placements;

    // Call once per frame after the placed lights were collected (PlacedLightIndex scans on Collect).
    public void Update(in GeometryRenderExport export, PlacedLightIndex placedLights)
    {
        ArgumentNullException.ThrowIfNull(placedLights);
        long version = export.StaticContentVersion;
        if (_hasScan && version != 0 && version == _staticVersion && placedLights.StaticRescanCount == _lightRescans)
        {
            return;
        }

        _ground.Rebuild(in export);
        PropLayout.Build(_ground, placedLights.Placements, _placements);
        _staticVersion = version;
        _lightRescans = placedLights.StaticRescanCount;
        _hasScan = true;
        RebuildCount++;
    }

    // Tests: the same build without an export.
    public void Rebuild(StaticGround ground, IReadOnlyList<PlacedLight> braziers)
    {
        PropLayout.Build(ground, braziers, _placements);
        RebuildCount++;
    }
}
