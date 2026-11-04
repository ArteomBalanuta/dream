using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

// Which material a generated cell gets. The geometry walk, the cell insertion order and the
// landmark heights are shared by every policy (WorldGenerator.CreateStartupGeometry); only the
// material choice differs, so two policies cannot disagree about the shape of the world.
//
// A policy is part of a world generator's identity: a world row stores its generator key and
// version, so the cells a given (key, version) produces must stay byte-identical forever.
// Implementations must be deterministic and allocation-free per cell.
public interface ITerrainMaterialPolicy
{
    GeometryMaterialId ResolveFloorMaterial(int x, int z, int width, int depth);

    GeometryMaterialId ResolveRaisedMaterial(int x, int z, int width, int depth, int y, int raisedHeight);
}
