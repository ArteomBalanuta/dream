using Dream.Rendering.Lighting;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

// The point lights of the lamp blocks in a render export: one light at each lamp cube's center.
//
// Static lamps (static coarse cubes, GeometryRenderExport.CompatibilityCubes) are rescanned only
// when the export's StaticContentVersion changes, into a reused list; an unknown version (0) is
// rescanned every frame. Export.Cells is not the source: it mixes the cached static cells with
// this frame's dynamic cells. Dynamic lamps (placed cubes, DynamicCompatibilityCubes) move, so
// they are scanned every frame. Fracture pieces and rubble give no light.
//
// Steady state allocates nothing: the lists are indexed, and the static list is reused.
public sealed class LampLightIndex
{
    public const float LampRadius = 10f;
    public static readonly Vector3 LampColor = new Vector3(1f, 0.78f, 0.5f) * 1.8f;

    private readonly List<Vector3> _staticLampCenters = [];
    private long _staticVersion;
    private bool _hasStaticScan;

    // Times the static lamps were rescanned (tests).
    public int StaticRescanCount { get; private set; }

    public int StaticLampCount => _staticLampCenters.Count;

    // Lamps added by the last Collect, static and dynamic (before the collector's capacity).
    public int LastLampCount { get; private set; }

    public void Collect(in GeometryRenderExport export, LightCollector lights)
    {
        ArgumentNullException.ThrowIfNull(lights);
        long version = export.StaticContentVersion;
        if (!_hasStaticScan || version == 0 || version != _staticVersion)
        {
            RescanStatic(export.CompatibilityCubes);
            _staticVersion = version;
            _hasStaticScan = true;
        }

        for (int i = 0; i < _staticLampCenters.Count; i++)
        {
            lights.Add(CreateLight(_staticLampCenters[i]));
        }

        int lampCount = _staticLampCenters.Count;
        IReadOnlyList<Cube>? dynamicCubes = export.DynamicCompatibilityCubes;
        if (dynamicCubes != null)
        {
            for (int i = 0; i < dynamicCubes.Count; i++)
            {
                Cube cube = dynamicCubes[i];
                if (cube.MaterialId == GeometryMaterialId.Lamp)
                {
                    lights.Add(CreateLight(cube.Position));
                    lampCount++;
                }
            }
        }

        LastLampCount = lampCount;
    }

    public static PointLight CreateLight(Vector3 center)
    {
        return new PointLight(center, LampRadius, LampColor);
    }

    private void RescanStatic(IReadOnlyList<Cube>? staticCubes)
    {
        StaticRescanCount++;
        _staticLampCenters.Clear();
        if (staticCubes == null)
        {
            return;
        }

        for (int i = 0; i < staticCubes.Count; i++)
        {
            Cube cube = staticCubes[i];
            if (cube.MaterialId == GeometryMaterialId.Lamp)
            {
                _staticLampCenters.Add(cube.Position);
            }
        }
    }
}
