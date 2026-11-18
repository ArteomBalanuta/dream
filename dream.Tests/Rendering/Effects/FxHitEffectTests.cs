using Dream.Rendering.Effects;
using Dream.Simulation.Combat;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// What a hit looks like: three kinds that differ at a glance, short-lived, within the pool reserve,
// never bright enough to pin a channel, and free of allocations.
public class FxHitEffectTests
{
    private const float HitFlash = 27f;
    private const float HitStar = 28f;
    private const float HitSlash = 29f;
    private const float HitSplat = 30f;

    private static readonly Vector3 Camera = new(10f, 2f, 10f);
    private static readonly Vector3 Anchor = new(10f, 1f, 4f);
    private static readonly FxViewBasis Basis = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    private static FxFrameInputs Inputs(float dt, float night = 0f) =>
        new(dt, Camera, -Vector3.UnitZ, Camera, night, Basis);

    private static FxInstance[] Spawn(FloatingCombatTextKind kind, out int count, float night = 0f, uint seed = 7u)
    {
        FxSimulation simulation = new(seed);
        simulation.SpawnHit(Anchor, kind);
        FxFrameInputs inputs = Inputs(0f, night);
        simulation.Advance(in inputs, []);
        FxInstance[] all = new FxInstance[FxLimits.MaxInstances];
        count = simulation.WriteInstances(all);
        return all;
    }

    private static int Marks(FxInstance[] instances, int count, float frame)
    {
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            if (instances[i].Frame == frame)
            {
                n++;
            }
        }

        return n;
    }

    [Fact]
    public void Each_Kind_Draws_Its_Own_Marks()
    {
        FxInstance[] regular = Spawn(FloatingCombatTextKind.Regular, out int r);
        Assert.Equal(1, Marks(regular, r, HitFlash));
        Assert.Equal(1, Marks(regular, r, HitSlash));
        Assert.Equal(0, Marks(regular, r, HitStar));
        Assert.Equal(0, Marks(regular, r, HitSplat));

        FxInstance[] critical = Spawn(FloatingCombatTextKind.Critical, out int c);
        Assert.Equal(1, Marks(critical, c, HitStar));
        Assert.Equal(1, Marks(critical, c, HitFlash));
        Assert.Equal(2, Marks(critical, c, HitSlash));
        Assert.Equal(0, Marks(critical, c, HitSplat));

        FxInstance[] incoming = Spawn(FloatingCombatTextKind.Incoming, out int n);
        Assert.Equal(1, Marks(incoming, n, HitSplat));
        Assert.Equal(1, Marks(incoming, n, HitFlash));
        Assert.Equal(0, Marks(incoming, n, HitStar));
        Assert.Equal(0, Marks(incoming, n, HitSlash));
    }

    // No Advance, so no dust: the pool holds exactly what the hit spawned.
    private static int Particles(FloatingCombatTextKind kind)
    {
        FxSimulation simulation = new(7u);
        simulation.SpawnHit(Anchor, kind);
        return simulation.ActiveParticles;
    }

    [Fact]
    public void A_Critical_Is_Heavier_And_The_Counts_Stay_Small()
    {
        int regular = Particles(FloatingCombatTextKind.Regular);
        int critical = Particles(FloatingCombatTextKind.Critical);
        int incoming = Particles(FloatingCombatTextKind.Incoming);

        Assert.Equal(10, regular);
        Assert.Equal(23, critical);
        Assert.Equal(10, incoming);
        Assert.True(critical <= FxSimulation.MaxHitParticles);
    }

    [Fact]
    public void Incoming_Is_Red_And_Dealt_Damage_Is_Not()
    {
        FxInstance[] incoming = Spawn(FloatingCombatTextKind.Incoming, out int n);
        FxInstance[] regular = Spawn(FloatingCombatTextKind.Regular, out int r);

        uint splat = ColourOf(incoming, n, HitSplat);
        uint slash = ColourOf(regular, r, HitSlash);
        // Red dominates the splat (green and blue are a small fraction of it); the amber slash keeps green.
        Assert.True(((splat >> 8) & 0xFF) * 3 < (splat & 0xFF), $"splat {splat:X8}");
        Assert.True(((slash >> 8) & 0xFF) * 3 > (slash & 0xFF) * 2, $"slash {slash:X8}");
    }

    private static uint ColourOf(FxInstance[] instances, int count, float frame)
    {
        for (int i = 0; i < count; i++)
        {
            if (instances[i].Frame == frame)
            {
                return instances[i].Color;
            }
        }

        throw new InvalidOperationException($"no mark with frame {frame}");
    }

    [Theory]
    [InlineData(FloatingCombatTextKind.Regular)]
    [InlineData(FloatingCombatTextKind.Critical)]
    [InlineData(FloatingCombatTextKind.Incoming)]
    public void Marks_Are_Gone_Within_A_Quarter_Second(FloatingCombatTextKind kind)
    {
        FxSimulation simulation = new(9u);
        simulation.SpawnHit(Anchor, kind);
        FxFrameInputs step = Inputs(1f / 60f);
        FxInstance[] all = new FxInstance[FxLimits.MaxInstances];
        for (int i = 0; i < 15; i++)
        {
            simulation.Advance(in step, []);
        }

        int count = simulation.WriteInstances(all);
        Assert.Equal(0, Marks(all, count, HitFlash) + Marks(all, count, HitStar) + Marks(all, count, HitSlash) + Marks(all, count, HitSplat));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void No_Packed_Channel_Pins_At_255_At_Any_Time_Of_Day(float night)
    {
        foreach (FloatingCombatTextKind kind in new[] { FloatingCombatTextKind.Regular, FloatingCombatTextKind.Critical, FloatingCombatTextKind.Incoming })
        {
            FxSimulation simulation = new(5u);
            simulation.SpawnHit(Anchor, kind);
            FxFrameInputs step = Inputs(0.01f, night);
            FxInstance[] instances = new FxInstance[FxLimits.MaxInstances];
            for (int frame = 0; frame < 20; frame++)
            {
                simulation.Advance(in step, []);
                int count = simulation.WriteInstances(instances);
                for (int i = 0; i < count; i++)
                {
                    uint c = instances[i].Color;
                    if (instances[i].Frame >= 8f && instances[i].Frame < 12f)
                    {
                        continue; // ambient dust
                    }

                    Assert.True((c & 0xFF) < 255 && ((c >> 8) & 0xFF) < 255 && ((c >> 16) & 0xFF) < 255, $"{kind} frame {frame} slot {i}");
                }
            }
        }
    }

    [Fact]
    public void Hit_Spam_Never_Eats_The_Ambient_Reserve()
    {
        FxSimulation simulation = new(11u);
        for (int i = 0; i < 400; i++)
        {
            simulation.SpawnHit(Anchor, (FloatingCombatTextKind)(i % 3));
        }

        Assert.True(simulation.ActiveParticles <= FxLimits.ParticleCapacity - FxSimulation.CastPoolReserve);
        Assert.True(simulation.DroppedSpawns > 0);

        // The reserve is still free for an ambient spawn.
        int before = simulation.ActiveParticles;
        simulation.SpawnDeath(Anchor);
        Assert.True(simulation.ActiveParticles > before);
    }

    [Fact]
    public void Spawning_Hits_Allocates_Nothing()
    {
        FxSimulation simulation = new(13u);
        for (int i = 0; i < 3; i++)
        {
            simulation.SpawnHit(Anchor, (FloatingCombatTextKind)i); // warm up
        }

        AllocationAssert.AtMost(0, () =>
        {
            simulation.SpawnHit(Anchor, FloatingCombatTextKind.Regular);
            simulation.SpawnHit(Anchor, FloatingCombatTextKind.Critical);
            simulation.SpawnHit(Anchor, FloatingCombatTextKind.Incoming);
        });
    }

    [Fact]
    public void Same_Seed_Gives_The_Same_Hit()
    {
        FxInstance[] a = Spawn(FloatingCombatTextKind.Critical, out int ca, seed: 21u);
        FxInstance[] b = Spawn(FloatingCombatTextKind.Critical, out int cb, seed: 21u);
        Assert.Equal(ca, cb);
        for (int i = 0; i < ca; i++)
        {
            Assert.Equal(a[i].Color, b[i].Color);
            Assert.Equal(a[i].Rotation, b[i].Rotation);
        }
    }
}
