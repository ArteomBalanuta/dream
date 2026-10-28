using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

public partial class Map
{
    public void AssignReplicatedCubeId(Cube cube, int actorId)
    {
        ArgumentNullException.ThrowIfNull(cube);
        if (_replicatedIdsByCube.TryGetValue(cube, out int previousId))
        {
            _cubesByReplicatedId.Remove(previousId);
        }

        if (_cubesByReplicatedId.TryGetValue(actorId, out Cube? previousCube) && !ReferenceEquals(previousCube, cube))
        {
            _replicatedIdsByCube.Remove(previousCube);
        }

        _cubesByReplicatedId[actorId] = cube;
        _replicatedIdsByCube[cube] = actorId;
    }

    public bool TryGetCubeByReplicatedId(int actorId, out Cube? cube)
    {
        if (_cubesByReplicatedId.TryGetValue(actorId, out cube) && _cubeCells.ContainsKey(cube))
        {
            return true;
        }

        cube = null;
        return false;
    }

    public bool TryGetReplicatedId(Cube cube, out int replicatedId)
    {
        ArgumentNullException.ThrowIfNull(cube);
        return _replicatedIdsByCube.TryGetValue(cube, out replicatedId);
    }
}
