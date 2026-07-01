using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.World;


public class MapRemovalTests
{
    [Fact]
    public void RemoveCube_Removes_Placed_Cube_From_World_Collections()
    {
        Map map = new(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        Cube cube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);

        bool removed = map.RemoveCube(cube);

        Assert.True(removed);
        Assert.Null(map.Cubes[3, 1, 3]);
        Assert.DoesNotContain(cube, map.SolidCubes);
        Assert.DoesNotContain(cube, map.GetNearbySolidCubes(new Vector3(3f, 1f, 3f), 8f));
    }

    [Fact]
    public void RemoveCube_Does_Not_Remove_Floor_Cube()
    {
        Map map = new(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        Cube floorCube = Assert.IsType<Cube>(map.Cubes[3, 0, 3]);

        bool removed = map.RemoveCube(floorCube);

        Assert.False(removed);
        Assert.NotNull(map.Cubes[3, 0, 3]);
    }

    [Fact]
    public void RemoveCube_Removes_Placed_Cube_Even_After_Position_Drift()
    {
        Map map = new(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        Cube cube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);
        cube.Position = new Vector3(4.1f, 1.5f, 3.5f);

        bool removed = map.RemoveCube(cube);

        Assert.True(removed);
        Assert.Null(map.Cubes[3, 1, 3]);
        Assert.DoesNotContain(cube, map.SolidCubes);
    }

    [Fact]
    public void TryGetFirstSolidCubeAlongRay_Ignores_Floor_And_Finds_Placed_Cube()
    {
        Map map = new(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);

        Cube? hit = map.TryGetFirstSolidCubeAlongRay(
            new Vector3(3.5f, 1.5f, 5.5f),
            -Vector3.UnitZ,
            maxDistance: 4f);

        Assert.NotNull(hit);
        Assert.Equal(new Vector3(3.5f, 1.5f, 3.5f), hit.Position);
    }
}

public class WorldObjectRegistryTests
{
    [Fact]
    public void Snapshot_Restores_Remaining_Health_To_Copied_Placed_Cube()
    {
        Map source = new(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        source.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        Cube sourceCube = Assert.IsType<Cube>(source.Cubes[3, 1, 3]);

        WorldObjectRegistry registry = new();
        WorldObjectRecord record = registry.RegisterCube(sourceCube, source, maxHealth: 50);
        record.ApplyDamage(20);
        WorldObjectCubeSnapshot snapshot = registry.CreateCubeSnapshot();

        Map target = new(width: 10, height: 8, depth: 10, loadVisualAssets: false);
        source.CopyPlacedCubesTo(target);
        registry.RestoreCubeSnapshot(target, snapshot);

        Cube targetCube = Assert.IsType<Cube>(target.Cubes[3, 1, 3]);
        WorldObjectRecord restored = Assert.IsType<WorldObjectRecord>(registry.TryGet(targetCube));
        Assert.Equal(30, restored.Health.CurrentHealth);
        Assert.Equal(50, restored.Health.MaxHealth);
    }

    [Fact]
    public void CombatAnchor_Is_Top_Centered_Above_Cube()
    {
        Map map = new(width: 8, height: 8, depth: 8, loadVisualAssets: false);
        map.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false);
        Cube cube = Assert.IsType<Cube>(map.Cubes[3, 1, 3]);

        WorldObjectRegistry registry = new();
        WorldObjectRecord record = registry.RegisterCube(cube, map);

        Vector3 anchor = record.GetCombatAnchorWorldPosition();

        Assert.Equal(cube.Position.X, anchor.X);
        Assert.True(anchor.Y > cube.Position.Y + (cube.Side * 0.5f));
        Assert.Equal(cube.Position.Z, anchor.Z);
    }

    [Fact]
    public void Register_Can_Store_A_NonCube_World_Object_With_Custom_Lifecycle()
    {
        object prop = new();
        bool removed = false;
        WorldObjectRegistry registry = new();

        WorldObjectRecord record = registry.Register(
            prop,
            () => new Vector3(7f, 8f, 9f),
            () =>
            {
                removed = true;
                return true;
            },
            maxHealth: 25);

        Assert.Equal(new Vector3(7f, 8f, 9f), record.GetCombatAnchorWorldPosition());
        Assert.True(registry.TryRemove(record));
        Assert.True(removed);
        Assert.Null(registry.TryGet(prop));
    }
}
