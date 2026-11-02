using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;
using Dream.Simulation.Combat;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// The effects simulation runs inside the render loop. Events reach it through
// SceneEventHub.Publish, which copies its handler list per event and is not part of that loop, so
// these tests call the simulation directly.
public class FxAllocationTests
{
    // A real basis: a default one leaves every spark square and skips the streak maths.
    private static readonly FxViewBasis Basis = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    private static PointLight[] FullLightList()
    {
        PointLight[] lights = new PointLight[LightCollector.Capacity];
        for (int i = 0; i < lights.Length; i++)
        {
            Vector3 position = new(8f + (i % 32) * 0.5f, 2f, 2f + (i / 32) * 0.5f);
            lights[i] = (i % 3) switch
            {
                0 => new PointLight(position, LampLightIndex.LampRadius, LampLightIndex.LampColor),
                1 => new PointLight(position, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor),
                _ => new PointLight(position, HeldLightCatalog.LanternRadius, HeldLightCatalog.LanternColor),
            };
        }

        return lights;
    }

    private static FxSimulation FullPoolSimulation()
    {
        FxSimulation simulation = new(17u);
        for (int i = 0; i < 200; i++)
        {
            simulation.SpawnDeath(new Vector3(10f, 1f, 4f));
        }

        Assert.Equal(FxLimits.ParticleCapacity, simulation.ActiveParticles);
        return simulation;
    }

    [Fact]
    public void Advance_And_WriteInstances_Allocate_Nothing_With_A_Full_Pool_And_1024_Lights()
    {
        FxSimulation simulation = FullPoolSimulation();
        PointLight[] lights = FullLightList();
        FxInstance[] destination = new FxInstance[FxLimits.MaxInstances];
        FxFrameInputs inputs = new(1f / 60f, new Vector3(10f, 2f, 10f), -Vector3.UnitZ, new Vector3(10f, 1.5f, 8f), 0.6f, Basis);

        AllocationAssert.AtMost(0, () =>
        {
            simulation.Advance(in inputs, lights);
            if (simulation.WriteInstances(destination) < 0)
            {
                throw new InvalidOperationException();
            }
        });
    }

    [Fact]
    public void Spawning_On_A_Full_Pool_Allocates_Nothing()
    {
        FxSimulation simulation = FullPoolSimulation();
        FxInstance[] destination = new FxInstance[FxLimits.MaxInstances];
        FxFrameInputs inputs = new(1f / 60f, new Vector3(10f, 2f, 10f), -Vector3.UnitZ, new Vector3(10f, 1.5f, 8f), 0f, Basis);
        Vector3 anchor = new(10f, 1f, 4f);

        AllocationAssert.AtMost(0, () =>
        {
            simulation.SpawnHit(anchor, FloatingCombatTextKind.Regular);
            simulation.SpawnHit(anchor, FloatingCombatTextKind.Critical);
            simulation.SpawnHit(anchor, FloatingCombatTextKind.Incoming);
            simulation.SpawnDeath(anchor);
            simulation.Advance(in inputs, []);
            if (simulation.WriteInstances(destination) < 0)
            {
                throw new InvalidOperationException();
            }
        });
        Assert.True(simulation.DroppedSpawns > 0);
    }
}
