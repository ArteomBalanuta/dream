using Dream.Rendering.Lighting;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

// The point lights of the lamp blocks in a render export.
//
// Static lamps come from the export's static render cells (GeometryRenderExport.StaticCells) whose
// material is Lamp: an intact lamp lights from its cube center, a partly mined (subdivided) lamp
// from the centroid of its remaining solid subcells, and a lamp with no solid subcell left gives
// none. They are rescanned only when the export's StaticContentVersion changes, into a reused list;
// an unknown version (0) is rescanned every frame. Export.Cells is not the source: it mixes the
// cached static cells with this frame's dynamic cells. An export without StaticCells (hand-built)
// falls back to the static coarse cubes (CompatibilityCubes). Dynamic lamps (placed cubes,
// DynamicCompatibilityCubes) move, so they are scanned every frame. Fracture pieces and rubble give
// no light.
//
// Only lamps within CollectDistance of the camera are added: the farthest drawn geometry
// (WorldVisibility.MaxRenderDistance) plus the lamp radius, so a lamp that could light any drawn
// surface is kept and the rest cannot flood the collector's 1,024 slots.
//
// Steady state allocates nothing: the lists are indexed, and the static list is reused.
public sealed class LampLightIndex
{
    public const float LampRadius = 10f;
    public const float CollectDistance = WorldVisibility.MaxRenderDistance + LampRadius;
    public static readonly Vector3 LampColor = new Vector3(1f, 0.78f, 0.5f) * 1.8f;

    private const float CollectDistanceSquared = CollectDistance * CollectDistance;

    private readonly List<Vector3> _staticLampCenters = [];
    private long _staticVersion;
    private bool _hasStaticScan;

    // Times the static lamps were rescanned (tests).
    public int StaticRescanCount { get; private set; }

    public int StaticLampCount => _staticLampCenters.Count;

    // Lamps added by the last Collect, static and dynamic, within CollectDistance (before the
    // collector's capacity).
    public int LastLampCount { get; private set; }

    public void Collect(in GeometryRenderExport export, Vector3 cameraPosition, LightCollector lights)
    {
        ArgumentNullException.ThrowIfNull(lights);
        long version = export.StaticContentVersion;
        if (!_hasStaticScan || version == 0 || version != _staticVersion)
        {
            RescanStatic(in export);
            _staticVersion = version;
            _hasStaticScan = true;
        }

        int lampCount = 0;
        for (int i = 0; i < _staticLampCenters.Count; i++)
        {
            Vector3 center = _staticLampCenters[i];
            if (IsWithinCollectDistance(center, cameraPosition))
            {
                lights.Add(CreateLight(center));
                lampCount++;
            }
        }

        IReadOnlyList<Cube>? dynamicCubes = export.DynamicCompatibilityCubes;
        if (dynamicCubes != null)
        {
            for (int i = 0; i < dynamicCubes.Count; i++)
            {
                Cube cube = dynamicCubes[i];
                if (cube.MaterialId == GeometryMaterialId.Lamp && IsWithinCollectDistance(cube.Position, cameraPosition))
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

    // The light position of a lamp cell: its cube center while intact, the centroid of its solid
    // subcells once subdivided (cells span [key, key + 1)). False when no subcell is solid.
    public static bool TryGetLampCenter(in GeometryRenderCell renderCell, out Vector3 center)
    {
        GeometryCellRecord cell = renderCell.Cell;
        if (cell.SubdivisionState == null)
        {
            center = renderCell.CompatibilityCube.Position;
            return true;
        }

        int resolution = cell.Definition.SubdivisionResolution;
        Vector3 sum = Vector3.Zero;
        int solid = 0;
        for (int x = 0; x < resolution; x++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int z = 0; z < resolution; z++)
                {
                    if (cell.IsChildSolid(new GeometrySubcellKey(x, y, z)))
                    {
                        sum += new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                        solid++;
                    }
                }
            }
        }

        if (solid == 0)
        {
            center = default;
            return false;
        }

        GeometryCellKey key = renderCell.Key;
        center = new Vector3(key.X, key.Y, key.Z) + (sum / (solid * (float)resolution));
        return true;
    }

    private static bool IsWithinCollectDistance(Vector3 position, Vector3 cameraPosition)
    {
        return Vector3.DistanceSquared(position, cameraPosition) <= CollectDistanceSquared;
    }

    private void RescanStatic(in GeometryRenderExport export)
    {
        StaticRescanCount++;
        _staticLampCenters.Clear();
        IReadOnlyList<GeometryRenderCell>? staticCells = export.StaticCells;
        if (staticCells != null)
        {
            for (int i = 0; i < staticCells.Count; i++)
            {
                GeometryRenderCell cell = staticCells[i];
                if (cell.Cell.Definition.MaterialId == GeometryMaterialId.Lamp &&
                    TryGetLampCenter(in cell, out Vector3 center))
                {
                    _staticLampCenters.Add(center);
                }
            }

            return;
        }

        IReadOnlyList<Cube>? staticCubes = export.CompatibilityCubes;
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
