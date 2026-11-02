using System.Runtime.InteropServices;
using Dream.Client.Presentation;
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
    // A real view basis for a camera looking down -Z. A default one gives every spark Stretch 1.
    private static readonly FxViewBasis Basis = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    private static FxFrameInputs Inputs(float dt = 1f / 60f, float night = 0f, Vector3? camera = null, Vector3? player = null)
    {
        Vector3 cam = camera ?? Camera;
        return new FxFrameInputs(dt, cam, Front, player ?? cam, night, Basis);
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

    private static bool IsHalo(in FxInstance instance) => instance.Frame >= (float)FxFrame.Halo && instance.Frame < (float)FxFrame.Flame0;

    private static bool IsFlame(in FxInstance instance) => instance.Frame >= (float)FxFrame.Flame0 && instance.Frame < (float)FxFrame.Blob;

    private static bool IsDust(in FxInstance instance) => instance.Frame >= (float)FxFrame.Dust0 && instance.Frame < (float)FxFrame.Ash0;

    private static bool IsEmber(in FxInstance instance) => instance.Frame >= (float)FxFrame.Ember0 && instance.Frame < (float)FxFrame.Dust0;

    private static bool IsSpark(in FxInstance instance) => instance.Frame < (float)FxFrame.Ember0;

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
        Assert.Equal(4, simulation.ActiveHalos); // three halos and the torch's flame
        Assert.Equal(3, halos.Length);
        Assert.Single(instances.Take(count), i => IsFlame(i));
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
        Assert.Equal(FxLimits.HaloBudget, instances.Take(count).Count(i => IsHalo(i) || IsFlame(i)));
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

    private static FxInstance[] WriteAll(FxSimulation simulation)
    {
        int count = Write(simulation, out FxInstance[] instances);
        return instances.Take(count).ToArray();
    }

    [Fact]
    public void Held_Fire_Is_Drawn_Below_The_Light_Which_Sits_Above_The_Shoulder()
    {
        Assert.True(FxSimulation.HeldFxDrop < HeldLightAnchor.BodyUp);
        Assert.InRange(FxSimulation.DepthBiasHeld, 0.2f, 0.55f);
        Vector3 at = Camera + Front * 5f;
        FxSimulation simulation = new(1u);

        simulation.Advance(Inputs(dt: 0f, night: 1f), [new PointLight(at, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor)]);
        FxInstance[] all = WriteAll(simulation);

        FxInstance halo = Assert.Single(all, i => IsHalo(i));
        FxInstance flame = Assert.Single(all, i => IsFlame(i));
        Assert.Equal(at.Y - FxSimulation.HeldFxDrop + 0.12f, halo.Position.Y, 1e-4f);
        Assert.Equal(at.X, halo.Position.X, 1e-4f);
        Assert.Equal((float)FxFrame.HaloCore, halo.Frame);
        Assert.Equal(FxSimulation.DepthBiasHeld, halo.DepthBias);
        Assert.InRange(halo.HalfSize, 0.55f, 0.85f);
        Assert.Equal(at.Y - FxSimulation.HeldFxDrop + 0.1f, flame.Position.Y, 1e-4f);
        Assert.Equal(0.20f, flame.HalfSize);
        Assert.Equal(0.55f, flame.DepthBias);
        Assert.Equal(0u, flame.Color >> 24);
        Assert.InRange(flame.Frame, (float)FxFrame.Flame0, (float)FxFrame.Flame0 + 3f);
    }

    [Fact]
    public void A_Lamp_Halo_Stays_On_The_Lamp_And_A_Lantern_Has_No_Flame()
    {
        Vector3 at = Camera + Front * 5f;
        FxSimulation simulation = new(1u);

        simulation.Advance(Inputs(dt: 0f, night: 1f),
        [
            new PointLight(at, LampLightIndex.LampRadius, LampLightIndex.LampColor),
            new PointLight(at + Vector3.UnitX * 2f, HeldLightCatalog.LanternRadius, HeldLightCatalog.LanternColor)
        ]);
        FxInstance[] all = WriteAll(simulation);

        FxInstance lamp = Assert.Single(all, i => IsHalo(i) && i.Frame == (float)FxFrame.Halo);
        Assert.Equal(at, lamp.Position);
        Assert.InRange(lamp.HalfSize, 1.2f, 1.3f);
        FxInstance lantern = Assert.Single(all, i => i.Frame == (float)FxFrame.HaloCore);
        Assert.Equal(0.55f, lantern.HalfSize);
        Assert.DoesNotContain(all, i => IsFlame(i));
    }

    [Fact]
    public void The_Torch_In_Hand_Draws_No_Halo_Or_Flame_But_Sheds_Embers_From_The_Light()
    {
        FxSimulation simulation = new(4u);
        Vector3 hand = Camera + Front * 0.4f;
        PointLight[] lights = [new PointLight(hand, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor)];

        for (int frame = 0; frame < 240; frame++)
        {
            simulation.Advance(Inputs(), lights);
        }

        FxInstance[] all = WriteAll(simulation);
        Assert.Equal(0, simulation.ActiveHalos);
        Assert.DoesNotContain(all, i => IsHalo(i) || IsFlame(i));
        Assert.Contains(all, i => IsEmber(i));
    }

    [Fact]
    public void A_Halo_Flickers_Over_Time_Without_Moving()
    {
        FxSimulation simulation = new(1u);
        PointLight[] lights = [new PointLight(Camera + Front * 5f, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor)];
        HashSet<uint> reds = [];

        for (int frame = 0; frame < 120; frame++)
        {
            simulation.Advance(Inputs(night: 1f), lights);
            reds.Add(WriteAll(simulation).First(i => IsHalo(i)).Color & 0xFFu);
        }

        Assert.True(reds.Count > 3, $"{reds.Count}");
    }

    [Fact]
    public void Halos_And_Flames_Together_Stay_Within_The_Halo_Budget()
    {
        FxSimulation simulation = new(1u);
        PointLight[] lights = new PointLight[LightCollector.Capacity];
        for (int i = 0; i < lights.Length; i++)
        {
            lights[i] = new PointLight(Camera + Front * 8f + new Vector3((i % 20) - 10f, 0f, (i / 20 % 20) - 10f) * 0.5f,
                HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor);
        }

        simulation.Advance(Inputs(dt: 0f, night: 1f), lights);
        FxInstance[] all = WriteAll(simulation);

        Assert.Equal(FxLimits.HaloBudget, simulation.ActiveHalos);
        Assert.Equal(FxLimits.HaloBudget, all.Count(i => IsHalo(i) || IsFlame(i)));
        Assert.True(all.Count(i => IsFlame(i)) > 0);
        Assert.True(all.Length <= FxLimits.MaxInstances);
    }

    [Fact]
    public void Dust_Is_Black_At_Night_With_No_Light()
    {
        FxSimulation simulation = Warm();
        simulation.Advance(Inputs(night: 1f), []);

        FxInstance[] dust = WriteAll(simulation).Where(i => IsDust(i)).ToArray();

        Assert.Equal(FxSimulation.DustTarget, dust.Length);
        Assert.All(dust, d => Assert.Equal(0u, d.Color));
    }

    [Fact]
    public void Dust_Is_Lit_Near_A_Torch_And_Black_Outside_Its_Radius_At_Night()
    {
        FxSimulation simulation = Warm();
        FxFrameInputs inputs = Inputs(night: 1f);
        Vector3 torch = FxSimulation.ViewCentre(in inputs);
        const float radius = 5f;
        simulation.Advance(in inputs, [new PointLight(torch, radius, new Vector3(1.6f, 1f, 0.5f))]);

        FxInstance[] dust = WriteAll(simulation).Where(i => IsDust(i)).ToArray();

        Assert.Contains(dust, d => Vector3.Distance(d.Position, torch) < radius * 0.6f && (d.Color & 0xFFu) > 0u);
        foreach (FxInstance d in dust.Where(d => Vector3.Distance(d.Position, torch) >= radius))
        {
            Assert.Equal(0u, d.Color);
        }

        // Warm light: red at least blue.
        Assert.All(dust, d => Assert.True((d.Color & 0xFFu) >= ((d.Color >> 16) & 0xFFu)));
    }

    [Fact]
    public void Dust_Is_Additive_And_Has_A_Daylight_Floor()
    {
        FxSimulation simulation = Warm();
        simulation.Advance(Inputs(night: 0f), []);

        FxInstance[] dust = WriteAll(simulation).Where(i => IsDust(i)).ToArray();

        Assert.All(dust, d => Assert.Equal(0u, d.Color >> 24));
        Assert.Contains(dust, d => (d.Color & 0xFFu) > 0u);
        Assert.All(dust, d => Assert.InRange(d.HalfSize, 0.028f, 0.05f));
    }

    [Fact]
    public void A_Spark_Streak_Follows_The_Screen_Velocity()
    {
        // Moving up the screen: rotated a quarter turn, stretched by speed * 0.03 / halfSize.
        FxSimulation.Streak(new Vector3(0f, 4f, 0f), in Basis, 0.05f, out float rotation, out float stretch);
        Assert.Equal(MathF.PI / 2f, rotation, 1e-5f);
        Assert.Equal(4f * 0.03f / 0.05f, stretch, 1e-4f);

        // Right and a long way in depth: only the screen part counts.
        FxSimulation.Streak(new Vector3(2f, 0f, -30f), in Basis, 0.05f, out rotation, out stretch);
        Assert.Equal(0f, rotation, 1e-5f);
        Assert.Equal(1.2f, stretch, 1e-4f);

        // Very fast: clamped.
        FxSimulation.Streak(new Vector3(-50f, 0f, 0f), in Basis, 0.025f, out rotation, out stretch);
        Assert.Equal(MathF.PI, MathF.Abs(rotation), 1e-5f);
        Assert.Equal(6f, stretch);
    }

    [Fact]
    public void A_Spark_Flying_Straight_At_The_Camera_Or_Under_A_Default_Basis_Stays_Square()
    {
        FxSimulation.Streak(Vector3.UnitZ * 8f, in Basis, 0.05f, out _, out float towards);
        FxSimulation.Streak(Vector3.Zero, in Basis, 0.05f, out _, out float still);
        FxSimulation.Streak(new Vector3(3f, 2f, 1f), default, 0.05f, out _, out float noBasis);

        Assert.Equal(1f, towards);
        Assert.Equal(1f, still);
        Assert.Equal(1f, noBasis);
    }

    [Fact]
    public void Written_Sparks_Are_Streaked_With_A_Real_Basis_And_Square_Without_One()
    {
        FxSimulation seen = new(9u);
        FxSimulation blind = new(9u);
        FxFrameInputs withBasis = Inputs(dt: 0.02f);
        FxFrameInputs withoutBasis = withBasis with { Basis = default };
        seen.SpawnHit(new Vector3(10f, 1f, 4f), FloatingCombatTextKind.Critical);
        blind.SpawnHit(new Vector3(10f, 1f, 4f), FloatingCombatTextKind.Critical);

        seen.Advance(in withBasis, []);
        blind.Advance(in withoutBasis, []);

        FxInstance[] a = WriteAll(seen).Where(i => IsSpark(i)).ToArray();
        FxInstance[] b = WriteAll(blind).Where(i => IsSpark(i)).ToArray();
        Assert.Equal(16, a.Length);
        Assert.Contains(a, i => i.Stretch > 1.5f);
        Assert.All(a, i => Assert.InRange(i.Stretch, 1f, 6f));
        Assert.All(b, i => Assert.Equal(1f, i.Stretch));
    }

    [Fact]
    public void Sparks_Are_Thrown_Away_From_The_Player()
    {
        FxSimulation simulation = new(6u);
        Vector3 anchor = new(10f, 1.2f, 4f);
        Vector3 player = new(10f, 1.5f, 10f); // south of the target, so away is -Z
        FxFrameInputs inputs = Inputs(dt: 0.05f, player: player);
        simulation.Advance(in inputs, []);
        for (int i = 0; i < 20; i++)
        {
            simulation.SpawnHit(anchor, FloatingCombatTextKind.Regular);
        }

        simulation.Advance(in inputs, []);
        FxInstance[] sparks = WriteAll(simulation).Where(i => IsSpark(i)).ToArray();

        Assert.Equal(160, sparks.Length);
        float meanZ = sparks.Average(i => i.Position.Z) - anchor.Z;
        Assert.True(meanZ < -0.03f, $"{meanZ}");
        Assert.All(sparks, i => Assert.InRange(i.HalfSize, FxSimulation.SparkEndHalfSize, FxSimulation.SparkStartHalfSize));
    }

    [Fact]
    public void Sparks_Start_Below_The_Anchor()
    {
        FxSimulation simulation = new(6u);
        Vector3 anchor = new(10f, 1.2f, 4f);
        simulation.Advance(Inputs(dt: 0f), []);
        simulation.SpawnHit(anchor, FloatingCombatTextKind.Regular);

        FxInstance[] sparks = WriteAll(simulation).Where(i => IsSpark(i)).ToArray();

        Assert.Equal(8, sparks.Length);
        Assert.All(sparks, i => Assert.Equal(anchor.Y - FxSimulation.AnchorDrop, i.Position.Y, 1e-4f));
    }

    [Fact]
    public void Ember_Colour_Is_Hotter_At_Birth_And_Redder_With_Age()
    {
        FxSimulation simulation = new(8u);
        simulation.SpawnDeath(new Vector3(10f, 1f, 4f));
        simulation.Advance(Inputs(dt: 0.1f), []);
        float young = GreenOverRed(WriteAll(simulation));
        for (int i = 0; i < 4; i++)
        {
            simulation.Advance(Inputs(dt: 0.1f), []);
        }

        float old = GreenOverRed(WriteAll(simulation));

        Assert.InRange(young, 0.45f, 0.65f);
        Assert.True(old < young - 0.05f, $"{young} {old}");
    }

    private static float GreenOverRed(FxInstance[] instances)
    {
        FxInstance[] embers = instances.Where(i => IsEmber(i) && (i.Color & 0xFFu) > 40u).ToArray();
        Assert.NotEmpty(embers);
        return (float)embers.Sum(i => (i.Color >> 8) & 0xFFu) / embers.Sum(i => i.Color & 0xFFu);
    }

    [Fact]
    public void Embers_Are_Pure_Additive_Light()
    {
        FxSimulation simulation = new(8u);
        simulation.SpawnDeath(new Vector3(10f, 1f, 4f));
        simulation.Advance(Inputs(dt: 0.1f), []);

        FxInstance[] embers = WriteAll(simulation).Where(i => IsEmber(i)).ToArray();

        Assert.Equal(20, embers.Length);
        Assert.All(embers, i => Assert.Equal(0u, i.Color >> 24));
        Assert.All(embers, i => Assert.True((i.Color & 0xFFu) >= ((i.Color >> 16) & 0xFFu)));
    }

    [Fact]
    public void A_Death_Carries_At_Least_Four_Times_The_Additive_Energy_Of_A_Regular_Hit()
    {
        Vector3 anchor = new(10f, 1f, 4f);
        FxSimulation hit = new(3u);
        FxSimulation death = new(3u);
        hit.SpawnHit(anchor, FloatingCombatTextKind.Regular);
        death.SpawnDeath(anchor);
        hit.Advance(Inputs(dt: 0.03f), []);
        death.Advance(Inputs(dt: 0.03f), []);

        float Energy(FxSimulation s) => WriteAll(s)
            .Where(i => !IsDust(i) && i.Color >> 24 == 0u)
            .Sum(i => ((i.Color & 0xFFu) + ((i.Color >> 8) & 0xFFu) + ((i.Color >> 16) & 0xFFu)) / 255f * i.HalfSize * i.HalfSize);

        Assert.True(Energy(death) >= 4f * Energy(hit), $"{Energy(death)} {Energy(hit)}");
    }

    [Fact]
    public void A_Death_Adds_A_Flash_And_Visible_Ash()
    {
        FxSimulation simulation = new(3u);
        simulation.SpawnDeath(new Vector3(10f, 1f, 4f));
        simulation.Advance(Inputs(dt: 0.05f), []);

        FxInstance[] all = WriteAll(simulation);

        FxInstance flash = Assert.Single(all, i => i.Frame == (float)FxFrame.Halo);
        Assert.InRange(flash.HalfSize, 0.8f, 1.5f);
        Assert.Equal(0.4f, flash.DepthBias);
        Assert.Equal(0u, flash.Color >> 24);
        FxInstance[] ash = all.Where(i => i.Frame >= (float)FxFrame.Ash0 && i.Frame < (float)FxFrame.Halo).ToArray();
        Assert.Equal(14, ash.Length);
        Assert.All(ash, i => Assert.True(i.Color >> 24 > 0u));
    }

    [Fact]
    public void The_Flash_Is_Gone_Within_A_Third_Of_A_Second()
    {
        FxSimulation simulation = new(3u);
        simulation.SpawnDeath(new Vector3(10f, 1f, 4f));

        for (int i = 0; i < 10; i++)
        {
            simulation.Advance(Inputs(dt: 0.04f), []);
        }

        Assert.DoesNotContain(WriteAll(simulation), i => i.Frame == (float)FxFrame.Halo);
    }

    [Fact]
    public void Lamps_Shed_Embers_Only_At_Night()
    {
        PointLight[] lights = [new PointLight(Camera + Front * 8f, LampLightIndex.LampRadius, LampLightIndex.LampColor)];
        FxSimulation day = new(5u);
        FxSimulation night = new(5u);

        int mostByDay = 0;
        int mostByNight = 0;
        for (int frame = 0; frame < 1800; frame++)
        {
            day.Advance(Inputs(night: 0f), lights);
            night.Advance(Inputs(night: 1f), lights);
            mostByDay = Math.Max(mostByDay, day.ActiveParticles);
            mostByNight = Math.Max(mostByNight, night.ActiveParticles);
        }

        // About one ember a second at 0.5 per second over 30 s; none by day.
        Assert.Equal(FxSimulation.DustTarget, mostByDay);
        Assert.True(mostByNight > FxSimulation.DustTarget);
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

    [Fact]
    public void A_Torch_Flame_At_Noon_Occludes_With_Alpha_And_Never_Reads_Dark()
    {
        Vector3 at = Camera + Front * 5f;
        PointLight torch = new(at, HeldLightCatalog.TorchRadius, HeldLightCatalog.TorchColor);
        FxSimulation noon = new(1u);
        noon.Advance(Inputs(dt: 0f, night: 0f), [torch]);
        FxSimulation night = new(1u);
        night.Advance(Inputs(dt: 0f, night: 1f), [torch]);

        FxInstance day = Assert.Single(WriteAll(noon), i => IsFlame(i));
        FxInstance dark = Assert.Single(WriteAll(night), i => IsFlame(i));

        // Not scaled by the 0.12 halo strength: a clearly visible flame with full instance alpha
        // (the atlas coverage peaks at 0.8), tinted orange: red held at the alpha, green and blue below.
        uint alpha = day.Color >> 24;
        Assert.Equal(255u, alpha);
        uint red = day.Color & 0xFFu;
        uint green = (day.Color >> 8) & 0xFFu;
        uint blue = (day.Color >> 16) & 0xFFu;
        Assert.True(red >= alpha);
        Assert.True(green < red && blue < green && blue > 0u);
        // Night is pure additive, as before.
        Assert.Equal(0u, dark.Color >> 24);
        Assert.True((dark.Color & 0xFFu) >= 190u);
    }

    [Fact]
    public void Ash_Is_Written_Before_The_Particles_Of_The_Same_Death_Whatever_The_Pool_Order()
    {
        FxSimulation simulation = new(11u);
        simulation.Advance(Inputs(dt: 0f), []);
        simulation.SpawnDeath(new Vector3(10f, 1f, 4f));
        simulation.Advance(Inputs(dt: 0.05f), []);
        FxInstance[] all = WriteAll(simulation);

        bool IsAsh(in FxInstance i) => i.Frame >= (float)FxFrame.Ash0 && i.Frame < (float)FxFrame.Halo;
        int lastAsh = Array.FindLastIndex(all, i => IsAsh(i));
        int firstOther = Array.FindIndex(all, i => !IsAsh(i));
        Assert.True(lastAsh >= 0 && firstOther > lastAsh, $"ash ends at {lastAsh}, others start at {firstOther}");
        Assert.Equal(14, all.Count(i => IsAsh(i)));

        // Removing the first-spawned particles (swap-removal) and writing again keeps the order.
        for (int i = 0; i < 40; i++)
        {
            simulation.Advance(Inputs(dt: 0.05f), []);
        }

        FxInstance[] later = WriteAll(simulation);
        int lastAshLater = Array.FindLastIndex(later, i => IsAsh(i));
        int firstOtherLater = Array.FindIndex(later, i => !IsAsh(i));
        Assert.True(lastAshLater < 0 || firstOtherLater > lastAshLater);
    }
}
