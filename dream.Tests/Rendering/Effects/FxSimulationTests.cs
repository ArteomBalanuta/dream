using System.Runtime.InteropServices;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;
using Dream.Simulation.Combat;
using Dream.Simulation.Runtime;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

public class FxSimulationTests
{
    private static readonly Vector3 Camera = new(10f, 2f, 10f);
    private static readonly Vector3 Front = -Vector3.UnitZ;

    private static FxFrameInputs Inputs(float dt = 1f / 60f, float night = 0f, Vector3? camera = null, Vector3? player = null)
    {
        Vector3 cam = camera ?? Camera;
        return new FxFrameInputs(dt, cam, Front, player ?? cam, night);
    }

    private static FxSimulation Warm(uint seed = 7u)
    {
        FxSimulation simulation = new(seed);
        // Fills the dust to its target (12 motes a frame).
        for (int i = 0; i < 12; i++)
        {
            simulation.Advance(Inputs(), []);
        }

        return simulation;
    }

    private static int Write(FxSimulation simulation, out FxInstance[] instances)
    {
        instances = new FxInstance[FxLimits.MaxInstances];
        return simulation.WriteInstances(instances);
    }

    private static bool IsHalo(in FxInstance instance) => instance.Frame >= (float)FxFrame.Halo;

    [Fact]
    public void ViewCentre_Is_Six_Metres_Ahead_In_First_Person()
    {
        FxFrameInputs inputs = Inputs();

        Vector3 centre = FxSimulation.ViewCentre(in inputs);

        Assert.True((centre - (Camera + Front * FxSimulation.MinViewDistance)).Length < 1e-4f);
    }

    [Fact]
    public void ViewCentre_Reaches_The_Player_When_The_Camera_Is_Far_Away()
    {
        Vector3 player = new(10f, 1.5f, 10f);
        Vector3 camera = player + new Vector3(0f, 0f, 18f); // looking straight at the player
        FxFrameInputs inputs = new(0.016f, camera, Front, player, 0f);

        Vector3 centre = FxSimulation.ViewCentre(in inputs);

        Assert.True((centre - player).Length < 1e-3f);
        Assert.InRange(Vector3.Distance(centre, camera), 17.9f, 18.1f);
    }

    [Fact]
    public void ViewCentre_Ignores_A_Player_Behind_The_Camera()
    {
        FxFrameInputs inputs = new(0.016f, Camera, Front, Camera + Vector3.UnitZ * 30f, 0f);

        Vector3 centre = FxSimulation.ViewCentre(in inputs);

        Assert.True((centre - (Camera + Front * FxSimulation.MinViewDistance)).Length < 1e-4f);
    }

    [Fact]
    public void Dust_Fills_To_About_Ninety_Six_Motes_And_Stays_There()
    {
        FxSimulation simulation = Warm();

        Assert.Equal(FxSimulation.DustTarget, simulation.ActiveParticles);
        for (int i = 0; i < 300; i++)
        {
            simulation.Advance(Inputs(), []);
        }

        Assert.Equal(FxSimulation.DustTarget, simulation.ActiveParticles);
    }

    [Fact]
    public void Dust_Follows_The_View_Centre_When_The_Camera_Moves()
    {
        FxSimulation simulation = Warm();
        Vector3 moved = Camera + new Vector3(500f, 0f, -300f);
        FxFrameInputs inputs = Inputs(camera: moved);
        simulation.Advance(in inputs, []);

        int count = Write(simulation, out FxInstance[] instances);
        Vector3 centre = FxSimulation.ViewCentre(in inputs);

        Assert.Equal(FxSimulation.DustTarget, count);
        for (int i = 0; i < count; i++)
        {
            Vector3 offset = instances[i].Position - centre;
            Assert.InRange(MathF.Abs(offset.X), 0f, FxSimulation.DustHalfExtents.X + 0.01f);
            Assert.InRange(MathF.Abs(offset.Y), 0f, FxSimulation.DustHalfExtents.Y + 0.01f);
            Assert.InRange(MathF.Abs(offset.Z), 0f, FxSimulation.DustHalfExtents.Z + 0.01f);
        }
    }

    [Fact]
    public void A_Hit_Spawns_Sparks_That_Fall_And_Expire()
    {
        FxSimulation simulation = Warm();
        int baseline = simulation.ActiveParticles;

        simulation.SpawnHit(new Vector3(10f, 1f, 4f), FloatingCombatTextKind.Regular);
        int spawned = simulation.ActiveParticles - baseline;
        Assert.InRange(spawned, 4, 20);

        for (int i = 0; i < 90; i++)
        {
            simulation.Advance(Inputs(), []);
        }

        Assert.Equal(baseline, simulation.ActiveParticles);
    }

    [Fact]
    public void A_Critical_Hit_Throws_More_Sparks_Than_A_Regular_One()
    {
        FxSimulation regular = Warm();
        FxSimulation critical = Warm();
        int before = regular.ActiveParticles;

        regular.SpawnHit(Vector3.Zero, FloatingCombatTextKind.Regular);
        critical.SpawnHit(Vector3.Zero, FloatingCombatTextKind.Critical);

        Assert.True(critical.ActiveParticles - before > regular.ActiveParticles - before);
    }

    [Fact]
    public void A_Death_Is_A_Bigger_Burst_Than_A_Hit_And_Includes_Ash()
    {
        FxSimulation hit = Warm();
        FxSimulation death = Warm();
        int before = hit.ActiveParticles;

        hit.SpawnHit(new Vector3(10f, 1f, 4f), FloatingCombatTextKind.Critical);
        death.SpawnDeath(new Vector3(10f, 1f, 4f));
        Write(death, out FxInstance[] instances);

        Assert.True(death.ActiveParticles - before > hit.ActiveParticles - before);
        Assert.Contains(instances.Take(death.ActiveParticles), i => i.Frame >= (float)FxFrame.Ash0 && i.Frame < (float)FxFrame.Halo);
    }

    [Fact]
    public void Sparks_Fall_Under_Gravity()
    {
        FxSimulation simulation = new(3u);
        simulation.SpawnHit(new Vector3(0f, 50f, 0f), FloatingCombatTextKind.Regular);
        float meanBefore = MeanHeight(simulation, spark: true);

        simulation.Advance(Inputs(dt: 0.1f, camera: new Vector3(0f, 50f, 20f)), []);
        simulation.Advance(Inputs(dt: 0.1f, camera: new Vector3(0f, 50f, 20f)), []);
        float meanAfter = MeanHeight(simulation, spark: true);

        // Upward-biased launch, then gravity and drag: after 0.2 s the burst is still near the anchor.
        Assert.InRange(meanAfter, meanBefore - 3f, meanBefore + 3f);
        Assert.NotEqual(meanBefore, meanAfter);
    }

    private static float MeanHeight(FxSimulation simulation, bool spark)
    {
        int count = Write(simulation, out FxInstance[] instances);
        float sum = 0f;
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            bool isSpark = instances[i].Frame < (float)FxFrame.Ember0;
            if (isSpark == spark)
            {
                sum += instances[i].Position.Y;
                n++;
            }
        }

        return n == 0 ? 0f : sum / n;
    }

    [Fact]
    public void Delta_Time_Is_Clamped_To_A_Tenth_Of_A_Second()
    {
        FxSimulation slow = new(5u);
        FxSimulation clamped = new(5u);
        slow.SpawnDeath(new Vector3(10f, 1f, 4f));
        clamped.SpawnDeath(new Vector3(10f, 1f, 4f));

        slow.Advance(Inputs(dt: 30f), []);
        clamped.Advance(Inputs(dt: FxSimulation.MaxDeltaSeconds), []);

        Assert.Equal(clamped.ActiveParticles, slow.ActiveParticles);
        Assert.Equal(Bytes(clamped), Bytes(slow));
    }

    [Fact]
    public void A_Non_Finite_Or_Negative_Delta_Time_Advances_Nothing()
    {
        FxSimulation a = new(5u);
        FxSimulation b = new(5u);
        a.SpawnDeath(Vector3.Zero);
        b.SpawnDeath(Vector3.Zero);

        a.Advance(Inputs(dt: float.NaN), []);
        b.Advance(Inputs(dt: -4f), []);

        Assert.Equal(Bytes(a), Bytes(b));
    }

    [Fact]
    public void A_Full_Pool_Drops_Spawns_And_Counts_Them()
    {
        FxSimulation simulation = new(11u);

        for (int i = 0; i < 200; i++)
        {
            simulation.SpawnDeath(Vector3.Zero);
        }

        Assert.Equal(FxLimits.ParticleCapacity, simulation.ActiveParticles);
        Assert.True(simulation.DroppedSpawns > 0);
        Assert.Equal(FxLimits.ParticleCapacity, Write(simulation, out _));
    }

    [Fact]
    public void The_Same_Seed_And_Script_Replay_Bit_For_Bit()
    {
        Assert.Equal(Bytes(Scripted(21u)), Bytes(Scripted(21u)));
        Assert.NotEqual(Bytes(Scripted(21u)), Bytes(Scripted(22u)));
    }

    private static FxSimulation Scripted(uint seed)
    {
        FxSimulation simulation = new(seed);
        PointLight[] lights = [new PointLight(new Vector3(10f, 2f, 5f), HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor)];
        for (int frame = 0; frame < 120; frame++)
        {
            if (frame % 20 == 3)
            {
                simulation.SpawnHit(new Vector3(10f, 1f, 4f), FloatingCombatTextKind.Critical);
            }

            if (frame == 60)
            {
                simulation.SpawnDeath(new Vector3(9f, 1f, 4f));
            }

            simulation.Advance(Inputs(), lights);
        }

        return simulation;
    }

    private static byte[] Bytes(FxSimulation simulation)
    {
        int count = Write(simulation, out FxInstance[] instances);
        return MemoryMarshal.AsBytes(instances.AsSpan(0, count)).ToArray();
    }

    [Fact]
    public void Lights_Are_Classified_By_Radius()
    {
        FxSimulation simulation = new(1u);
        Vector3 p = Camera + Front * 5f;
        PointLight[] lights =
        [
            new PointLight(p, LampLightIndex.LampRadius, LampLightIndex.LampColor),
            new PointLight(p + Vector3.UnitX, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor),
            new PointLight(p + Vector3.UnitX * 2f, HeldLightCatalog.LanternRadius, HeldLightCatalog.LanternColor),
            new PointLight(p + Vector3.UnitX * 3f, 3.5f, Vector3.One),
            new PointLight(p + Vector3.UnitX * 4f, 20f, Vector3.One),
        ];

        simulation.Advance(Inputs(dt: 0f), lights);
        int count = Write(simulation, out FxInstance[] instances);

        FxInstance[] halos = instances.Take(count).Where(i => IsHalo(i)).ToArray();
        Assert.Equal(3, simulation.ActiveHalos);
        Assert.Equal(3, halos.Length);
        FxInstance lamp = Assert.Single(halos, h => h.Frame == (float)FxFrame.Halo);
        Assert.Equal(FxSimulation.DepthBiasLamp, lamp.DepthBias);
        Assert.All(halos.Where(h => h.Frame == (float)FxFrame.HaloCore), h => Assert.Equal(FxSimulation.DepthBiasHeld, h.DepthBias));
        Assert.Equal(2, halos.Count(h => h.Frame == (float)FxFrame.HaloCore));
        Assert.All(halos, h => Assert.Equal(0u, h.Color >> 24));
    }

    [Fact]
    public void Halos_Skip_The_Torch_In_Hand_And_Lights_Far_From_The_View()
    {
        FxSimulation simulation = new(1u);
        PointLight[] lights =
        [
            new PointLight(Camera + Vector3.UnitY * 0.5f, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor),
            new PointLight(Camera + Front * 6f + Vector3.UnitX * 40f, LampLightIndex.LampRadius, LampLightIndex.LampColor),
            new PointLight(Camera + Front * 6f + Vector3.UnitX * 25f, LampLightIndex.LampRadius, LampLightIndex.LampColor),
        ];

        simulation.Advance(Inputs(dt: 0f), lights);

        Assert.Equal(1, simulation.ActiveHalos);
    }

    [Fact]
    public void Halos_Fade_Out_Over_The_Last_Stretch_Before_The_Cull()
    {
        FxFrameInputs inputs = Inputs(dt: 0f);
        Vector3 centre = FxSimulation.ViewCentre(in inputs);
        uint Red(float distance)
        {
            FxSimulation simulation = new(1u);
            simulation.Advance(inputs, [new PointLight(centre + Vector3.UnitX * distance, LampLightIndex.LampRadius, LampLightIndex.LampColor)]);
            Assert.Equal(1, simulation.ActiveHalos);
            Write(simulation, out FxInstance[] instances);
            return instances.First(i => IsHalo(i)).Color & 0xFFu;
        }

        float fadeStart = FxSimulation.HaloMaxViewDistance - FxSimulation.HaloFadeDistance;
        uint full = Red(fadeStart - 1f);
        uint middle = Red(fadeStart + FxSimulation.HaloFadeDistance * 0.5f);
        uint edge = Red(FxSimulation.HaloMaxViewDistance - 0.05f);

        Assert.Equal(full, Red(10f));
        Assert.True(middle < full && middle > edge, $"{full} {middle} {edge}");
        Assert.True(edge <= 1u, $"{edge}");
    }

    [Fact]
    public void Halo_Count_Is_Capped_At_The_Budget_And_The_Scan_Favours_The_First_Lights()
    {
        FxSimulation simulation = new(1u);
        PointLight[] lights = new PointLight[LightCollector.Capacity];
        for (int i = 0; i < lights.Length; i++)
        {
            Vector3 position = Camera + Front * 6f + new Vector3((i % 20) - 10f, 0f, (i / 20 % 20) - 10f) * 0.5f;
            lights[i] = i == 0
                ? new PointLight(Camera + Front * 5f, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor)
                : new PointLight(position, LampLightIndex.LampRadius, LampLightIndex.LampColor);
        }

        simulation.Advance(Inputs(dt: 0f), lights);
        int count = Write(simulation, out FxInstance[] instances);

        Assert.Equal(FxLimits.HaloBudget, simulation.ActiveHalos);
        Assert.Equal(FxLimits.HaloBudget, instances.Take(count).Count(i => IsHalo(i)));
        Assert.Contains(instances.Take(count), i => i.Frame == (float)FxFrame.HaloCore);
        Assert.True(count <= FxLimits.MaxInstances);
    }

    [Fact]
    public void Halos_Are_Stronger_At_Night_And_Carry_The_Light_Colour()
    {
        PointLight[] lights = [new PointLight(Camera + Front * 5f, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor)];
        FxSimulation day = new(1u);
        FxSimulation night = new(1u);

        day.Advance(Inputs(dt: 0f, night: 0f), lights);
        night.Advance(Inputs(dt: 0f, night: 1f), lights);
        Write(day, out FxInstance[] dayInstances);
        Write(night, out FxInstance[] nightInstances);
        FxInstance dayHalo = dayInstances.First(i => IsHalo(i));
        FxInstance nightHalo = nightInstances.First(i => IsHalo(i));

        Assert.True((nightHalo.Color & 0xFF) > (dayHalo.Color & 0xFF));
        // Torch tint: red above green above blue, at both strengths.
        Assert.True((nightHalo.Color & 0xFF) > ((nightHalo.Color >> 8) & 0xFF));
        Assert.True(((nightHalo.Color >> 8) & 0xFF) > ((nightHalo.Color >> 16) & 0xFF));
    }

    [Fact]
    public void A_Brighter_Flicker_Gives_A_Brighter_Halo()
    {
        FxSimulation simulation = new(1u);
        PointLight dim = new(Camera + Front * 5f, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor * 0.92f);
        PointLight bright = new(Camera + Front * 5f, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor * 1.08f);

        simulation.Advance(Inputs(dt: 0f, night: 1f), [dim]);
        Write(simulation, out FxInstance[] a);
        simulation.Advance(Inputs(dt: 0f, night: 1f), [bright]);
        Write(simulation, out FxInstance[] b);

        Assert.True((b.First(i => IsHalo(i)).Color & 0xFF) > (a.First(i => IsHalo(i)).Color & 0xFF));
    }

    [Fact]
    public void Held_Torches_Shed_Embers_Over_Time()
    {
        FxSimulation simulation = Warm();
        PointLight[] lights = [new PointLight(Camera + Front * 5f, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor)];
        int baseline = simulation.ActiveParticles;

        for (int i = 0; i < 120; i++)
        {
            simulation.Advance(Inputs(), lights);
        }

        Assert.True(simulation.ActiveParticles > baseline);
        Assert.Equal(0L, simulation.DroppedSpawns);
    }

    [Fact]
    public void Written_Instances_Fade_Out_Over_A_Particles_Life()
    {
        FxSimulation simulation = new(2u);
        simulation.SpawnHit(new Vector3(10f, 1f, 4f), FloatingCombatTextKind.Regular);
        int count = Write(simulation, out FxInstance[] early);
        uint earlyRed = early.Take(count).Max(i => i.Color & 0xFFu);

        simulation.Advance(Inputs(dt: 0.1f), []);
        simulation.Advance(Inputs(dt: 0.1f), []);
        simulation.Advance(Inputs(dt: 0.1f), []);
        count = Write(simulation, out FxInstance[] late);
        uint lateRed = count == 0 ? 0u : late.Take(count).Max(i => i.Color & 0xFFu);

        Assert.True(earlyRed > lateRed);
    }

    [Fact]
    public void WriteInstances_Never_Writes_Past_A_Short_Destination()
    {
        FxSimulation simulation = Warm();
        FxInstance[] small = new FxInstance[10];

        int written = simulation.WriteInstances(small);

        Assert.Equal(10, written);
    }

    [Fact]
    public void WriteInstances_Starts_At_The_Given_Index_And_Never_Writes_Past_A_Short_Destination()
    {
        FxSimulation simulation = Warm();
        FxInstance[] small = new FxInstance[10];
        FxInstance sentinel = new() { HalfSize = 123f, Stretch = 7f };
        Array.Fill(small, sentinel);

        int written = simulation.WriteInstances(small, 4);

        Assert.Equal(10, written);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(123f, small[i].HalfSize);
        }

        for (int i = 4; i < 10; i++)
        {
            Assert.NotEqual(123f, small[i].HalfSize);
            Assert.Equal(0f, small[i].Stretch);
        }

        Assert.Equal(10, simulation.WriteInstances(small, 10));

        FxInstance[] roomy = new FxInstance[FxLimits.MaxInstances];
        int end = simulation.WriteInstances(roomy, 100);
        Assert.Equal(100 + simulation.ActiveParticles + simulation.ActiveHalos, end);
        Assert.Throws<ArgumentOutOfRangeException>(() => simulation.WriteInstances(small, 11));
    }

    [Fact]
    public void FxInstance_Packs_Red_In_The_Low_Byte_And_Clamps()
    {
        uint color = FxInstance.PackColor(1f, 0.5f, 0f, 2f);

        Assert.Equal(255u, color & 0xFF);
        Assert.Equal(128u, (color >> 8) & 0xFF);
        Assert.Equal(0u, (color >> 16) & 0xFF);
        Assert.Equal(255u, color >> 24);
        Assert.Equal(0u, FxInstance.PackColor(float.NaN, -1f, 0f, 0f));
    }

    [Fact]
    public void Random_Stays_In_Range_And_Is_Repeatable()
    {
        FxRandom a = new(99u);
        FxRandom b = new(99u);
        for (int i = 0; i < 1000; i++)
        {
            float value = a.NextFloat();
            Assert.InRange(value, 0f, 0.99999994f);
            Assert.Equal(value, b.NextFloat());
            Assert.InRange(a.Range(0, 4), 0, 3);
            Assert.InRange(a.UnitVector().Length, 0.999f, 1.001f);
            b.Range(0, 4);
            b.UnitVector();
        }

        FxRandom zero = new(0u);
        Assert.NotEqual(0u, zero.NextUInt());
    }
}
