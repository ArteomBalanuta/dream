using System.Runtime.InteropServices;
using Dream.Rendering.Effects;
using Dream.Simulation.Combat;
using Dream.Simulation.Runtime;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

public class FxSkillCastTests
{
    private static readonly Vector3 Camera = new(10f, 2f, 10f);
    private static readonly Vector3 Origin = new(10f, 1f, 4f);
    private static readonly FxViewBasis Basis = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    private static FxFrameInputs Inputs(float dt = 1f / 60f, float night = 0f) =>
        new(dt, Camera, -Vector3.UnitZ, Camera, night, Basis);

    // Steps the simulation in 1/64 s frames (one authoritative tick) until `seconds` have passed.
    private static void AdvanceTo(FxSimulation simulation, float seconds)
    {
        FxFrameInputs step = Inputs(1f / 64f);
        int steps = (int)MathF.Round(seconds * 64f);
        for (int i = 0; i < steps; i++)
        {
            simulation.Advance(in step, []);
        }
    }

    // Just past the active window: a Cleave has emitted everything it is going to.
    private const float SweepDone = MeleeArcSwing.ActiveEndSeconds + 1f / 64f;

    // A Cleave draws nothing until its swing turns active, so an Arc cast is stepped through the sweep first.
    // Other kinds are written at once with no ageing (so no dust: the pool holds exactly what the cast spawned).
    private static FxInstance[] Cast(SkillFxKind kind, out int count, Vector3? direction = null, float range = 3.5f, float night = 0f, uint seed = 3u)
    {
        FxSimulation simulation = new(seed);
        simulation.SpawnSkillCast(kind, Origin, direction ?? Vector3.UnitX, range);
        if (kind == SkillFxKind.Arc)
        {
            AdvanceTo(simulation, SweepDone);
        }

        return Written(simulation, out count, night);
    }

    private static FxInstance[] Written(FxSimulation simulation, out int count, float night = 0f)
    {
        // One zero-length step puts the frame inputs (the night factor) in without ageing anything.
        FxFrameInputs inputs = Inputs(0f, night);
        simulation.Advance(in inputs, []);
        FxInstance[] all = new FxInstance[FxLimits.MaxInstances];
        count = simulation.WriteInstances(all);
        return all;
    }

    private static int CountFrames(FxInstance[] instances, int count, FxFrame first, int span = 1)
    {
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            if (instances[i].Frame >= (float)first && instances[i].Frame < (float)first + span)
            {
                n++;
            }
        }

        return n;
    }

    // Dust is topped up by Advance; only count what a cast alone put in.
    private static int CastCount(SkillFxKind kind, FxSimulation simulation)
    {
        int before = simulation.ActiveParticles;
        simulation.SpawnSkillCast(kind, Origin, Vector3.UnitX, 3.5f);
        if (kind == SkillFxKind.Arc)
        {
            AdvanceTo(simulation, SweepDone);
            FxInstance[] all = new FxInstance[FxLimits.MaxInstances];
            int count = simulation.WriteInstances(all);
            return simulation.ActiveParticles - before - CountFrames(all, count, FxFrame.Dust0, 4);
        }

        return simulation.ActiveParticles - before;
    }

    [Theory]
    [InlineData(SkillFxKind.Arc, 19)]
    [InlineData(SkillFxKind.Bolt, 18)]
    [InlineData(SkillFxKind.Mend, 15)]
    public void Each_Cast_Spawns_Its_Budgeted_Particle_Count(SkillFxKind kind, int expected)
    {
        FxSimulation simulation = new(1u);

        int spawned = CastCount(kind, simulation);

        Assert.Equal(expected, spawned);
        Assert.True(spawned <= FxSimulation.MaxCastParticles);
    }

    [Fact]
    public void The_Three_Casts_Use_Distinct_Frames()
    {
        FxInstance[] arc = Cast(SkillFxKind.Arc, out int arcCount);
        FxInstance[] bolt = Cast(SkillFxKind.Bolt, out int boltCount);
        FxInstance[] mend = Cast(SkillFxKind.Mend, out int mendCount);

        Assert.Equal(1, CountFrames(arc, arcCount, FxFrame.Arc));
        Assert.Equal(0, CountFrames(arc, arcCount, FxFrame.Ring));
        Assert.Equal(0, CountFrames(arc, arcCount, FxFrame.Bolt0, 2));
        Assert.Equal(12, CountFrames(bolt, boltCount, FxFrame.Bolt0, 2));
        Assert.Equal(0, CountFrames(bolt, boltCount, FxFrame.Arc));
        Assert.Equal(1, CountFrames(mend, mendCount, FxFrame.Ring));
        Assert.Equal(0, CountFrames(mend, mendCount, FxFrame.Arc));
        Assert.Equal(14, CountFrames(mend, mendCount, FxFrame.Ember0, 4));
    }

    [Fact]
    public void Ground_Quads_Are_Flat_And_Written_Before_Everything_Else()
    {
        foreach (SkillFxKind kind in new[] { SkillFxKind.Arc, SkillFxKind.Mend })
        {
            FxInstance[] instances = Cast(kind, out int count);

            Assert.Equal(FxInstance.GroundAligned, instances[0].Stretch);
            Assert.True(instances[0].Frame == (float)FxFrame.Arc || instances[0].Frame == (float)FxFrame.Ring);
            for (int i = 1; i < count; i++)
            {
                Assert.NotEqual(FxInstance.GroundAligned, instances[i].Stretch);
            }
        }
    }

    [Fact]
    public void Ground_Quads_Draw_Under_The_Particles_Even_When_Pool_Slots_Are_Shuffled()
    {
        FxSimulation simulation = new(9u);
        simulation.SpawnSkillCast(SkillFxKind.Mend, Origin, Vector3.UnitX, 0f);
        // Expire nothing, but spawn sparks after the ring so the ring is not slot 0 of a later cast.
        simulation.SpawnSkillCast(SkillFxKind.Bolt, Origin, Vector3.UnitX, 18f);
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        AdvanceTo(simulation, SweepDone);

        FxInstance[] instances = Written(simulation, out int count);

        int lastGround = -1;
        int firstOther = count;
        for (int i = 0; i < count; i++)
        {
            if (instances[i].Stretch == FxInstance.GroundAligned)
            {
                lastGround = i;
            }
            else if (firstOther == count)
            {
                firstOther = i;
            }
        }

        Assert.Equal(1, lastGround);
        Assert.True(lastGround < firstOther);
    }

    [Fact]
    public void An_Arc_Cast_Spawns_Nothing_Before_The_Swing_Turns_Active()
    {
        FxSimulation simulation = new(2u);
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        Assert.Equal(0, simulation.ActiveParticles);

        // Up to the last frame before WindupSeconds: still nothing (dust aside).
        AdvanceTo(simulation, MeleeArcSwing.WindupSeconds - 1.5f / 64f);
        FxInstance[] before = new FxInstance[FxLimits.MaxInstances];
        int count = simulation.WriteInstances(before);
        Assert.True(count - CountFrames(before, count, FxFrame.Dust0, 4) == 0, "nothing but dust before the swing turns active");

        AdvanceTo(simulation, 2f / 64f);
        FxInstance[] after = new FxInstance[FxLimits.MaxInstances];
        count = simulation.WriteInstances(after);
        Assert.Equal(1, CountFrames(after, count, FxFrame.Arc));
    }

    [Fact]
    public void The_Arc_Quad_Is_Sized_So_Its_Painted_Edge_Lands_On_The_Range_And_Points_Along_The_Aim()
    {
        foreach (float range in new[] { 2f, 3.5f, 5f })
        {
            FxSimulation simulation = new(2u);
            simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, new Vector3(0f, 0f, -1f), range);
            AdvanceTo(simulation, MeleeArcSwing.WindupSeconds + 2f / 64f);
            FxInstance[] instances = Written(simulation, out int count);

            int quads = 0;
            for (int i = 0; i < count; i++)
            {
                if (instances[i].Frame != (float)FxFrame.Arc)
                {
                    continue;
                }

                quads++;
                Assert.Equal(FxArcGeometry.HalfSizeForRange(range), instances[i].HalfSize, 3);
                Assert.Equal(range, instances[i].HalfSize * FxArcGeometry.OuterRadiusFraction, 3);
                Assert.Equal(MathF.Atan2(-1f, 0f), instances[i].Rotation, 4);
                Assert.Equal(FxInstance.GroundAligned, instances[i].Stretch);
                // Feet, not the capsule centre.
                Assert.True(instances[i].Position.Y < Origin.Y - 0.5f);
            }

            Assert.Equal(1, quads);

            // The size is constant for the whole life, and fully visible by the end of the active window.
            AdvanceTo(simulation, MeleeArcSwing.ActiveEndSeconds - MeleeArcSwing.WindupSeconds - 2f / 64f);
            FxInstance[] later = Written(simulation, out count);
            int quad = Array.FindIndex(later, 0, count, x => x.Frame == (float)FxFrame.Arc);
            Assert.True(quad >= 0);
            Assert.Equal(FxArcGeometry.HalfSizeForRange(range), later[quad].HalfSize, 3);
        }
    }

    [Fact]
    public void The_Arc_Quad_Is_Fully_Opaque_By_The_End_Of_The_Active_Window_And_Then_Fades()
    {
        FxSimulation simulation = new(2u);
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        AdvanceTo(simulation, MeleeArcSwing.ActiveEndSeconds);
        FxFrameInputs hold = Inputs(0f, 0f);
        simulation.Advance(in hold, []);
        uint atEnd = ArcAlpha(simulation);
        AdvanceTo(simulation, 0.2f);
        uint later = ArcAlpha(simulation);

        // Day coverage is 0.7 of 255 when the crescent is fully in.
        Assert.InRange(atEnd, 170u, 180u);
        Assert.True(later < atEnd);
    }

    private static uint ArcAlpha(FxSimulation simulation)
    {
        FxInstance[] all = new FxInstance[FxLimits.MaxInstances];
        int count = simulation.WriteInstances(all);
        int quad = Array.FindIndex(all, 0, count, x => x.Frame == (float)FxFrame.Arc);
        return quad < 0 ? 0u : all[quad].Color >> 24;
    }

    [Fact]
    public void Sweep_Sparks_Ride_The_Edge_And_Move_From_The_Casters_Left_To_Right()
    {
        FxSimulation simulation = new(5u);
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        // The caster's right when facing +X is +Z (right = (-aim.Z, 0, aim.X)).
        AdvanceTo(simulation, MeleeArcSwing.WindupSeconds - 1f / 64f);
        Assert.Empty(SparkLaterals(simulation));

        AdvanceTo(simulation, MeleeArcSwing.ActiveSeconds + 1f / 64f);
        float[] lateral = SparkLaterals(simulation);

        Assert.Equal(14, lateral.Length);
        Assert.True(lateral[0] < -1.5f, $"first spark at {lateral[0]}");
        Assert.True(lateral[^1] > 1.5f, $"last spark at {lateral[^1]}");
        float firstHalf = lateral.Take(7).Average();
        float secondHalf = lateral.Skip(7).Average();
        Assert.True(firstHalf < -0.5f && secondHalf > 0.5f);
        for (int i = 1; i < lateral.Length; i++)
        {
            Assert.True(lateral[i] > lateral[i - 1] - 0.45f, $"spark {i} went back toward the left");
        }
    }

    [Fact]
    public void Sweep_Sparks_Sit_Near_Nine_Tenths_Of_The_Range()
    {
        FxSimulation simulation = new(5u);
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        AdvanceTo(simulation, MeleeArcSwing.WindupSeconds + 1f / 64f);
        FxInstance[] all = new FxInstance[FxLimits.MaxInstances];
        int count = simulation.WriteInstances(all);
        int found = 0;
        for (int i = 0; i < count; i++)
        {
            if (all[i].Frame >= (float)FxFrame.Spark0 && all[i].Frame < (float)FxFrame.Spark0 + 4)
            {
                float reach = MathF.Sqrt(MathF.Pow(all[i].Position.X - Origin.X, 2f) + MathF.Pow(all[i].Position.Z - Origin.Z, 2f));
                Assert.InRange(reach, 0.9f * 3.5f - 0.3f, 0.9f * 3.5f + 0.5f);
                found++;
            }
        }

        Assert.True(found >= 1);
    }

    // Lateral offsets (along the caster's right, +Z when facing +X) of every spark, in pool (emission) order.
    private static float[] SparkLaterals(FxSimulation simulation)
    {
        FxInstance[] all = new FxInstance[FxLimits.MaxInstances];
        int count = simulation.WriteInstances(all);
        List<float> lateral = [];
        for (int i = 0; i < count; i++)
        {
            if (all[i].Frame >= (float)FxFrame.Spark0 && all[i].Frame < (float)FxFrame.Spark0 + 4)
            {
                lateral.Add(all[i].Position.Z - Origin.Z);
            }
        }

        return lateral.ToArray();
    }

    [Fact]
    public void A_Full_Sweep_Array_Drops_The_Cast_And_Counts_It()
    {
        FxSimulation simulation = new(5u);
        for (int i = 0; i < FxSimulation.MaxPendingArcSweeps; i++)
        {
            simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        }

        Assert.Equal(0L, simulation.DroppedSpawns);
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        Assert.Equal(19L, simulation.DroppedSpawns);
    }

    [Fact]
    public void Ring_Expands_From_030_To_090_Metres_Over_Its_Life()
    {
        FxSimulation simulation = new(2u);
        simulation.SpawnSkillCast(SkillFxKind.Mend, Origin, Vector3.Zero, 0f);
        FxInstance[] start = Written(simulation, out _);
        Assert.Equal(0.3f, start[0].HalfSize, 3);

        FxFrameInputs step = Inputs(0.0999f);
        for (int i = 0; i < 5; i++)
        {
            simulation.Advance(in step, []);
        }

        FxInstance[] late = Written(simulation, out int count);
        Assert.True(late[0].HalfSize > 0.75f && late[0].HalfSize <= 0.9f);
        Assert.Equal(FxInstance.GroundAligned, late[0].Stretch);
        Assert.True(count > 1);
    }

    [Fact]
    public void Bolt_Darts_Fly_Along_The_Aim_At_Their_Own_Speeds_And_Stay_Within_The_Life_Cap()
    {
        FxSimulation simulation = new(4u);
        simulation.SpawnSkillCast(SkillFxKind.Bolt, Origin, Vector3.UnitX, 18f);
        FxFrameInputs step = Inputs(0.1f);

        simulation.Advance(in step, []);
        FxInstance[] instances = new FxInstance[FxLimits.MaxInstances];
        int count = simulation.WriteInstances(instances);
        float minX = float.MaxValue;
        float maxX = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            if (instances[i].Frame >= (float)FxFrame.Bolt0 && instances[i].Frame < (float)FxFrame.Bolt0 + 2)
            {
                minX = MathF.Min(minX, instances[i].Position.X);
                maxX = MathF.Max(maxX, instances[i].Position.X);
                Assert.True(instances[i].Stretch > 1f, "a dart must be streaked along its velocity");
            }
        }

        // 0.1 s at 24-36 m/s: the darts have strung out over about a metre.
        Assert.True(minX > Origin.X + 2f);
        Assert.True(maxX - minX > 0.5f);

        // The longest a dart lives is 0.8 s, then none are left.
        for (int i = 0; i < 9; i++)
        {
            simulation.Advance(in step, []);
        }

        FxInstance[] after = new FxInstance[FxLimits.MaxInstances];
        int left = simulation.WriteInstances(after);
        Assert.Equal(0, CountFrames(after, left, FxFrame.Bolt0, 2));
    }

    [Theory]
    [InlineData(SkillFxKind.Arc)]
    [InlineData(SkillFxKind.Bolt)]
    [InlineData(SkillFxKind.Mend)]
    public void A_Zero_Or_Non_Finite_Aim_Falls_Back_To_A_Radial_Burst_That_Is_Finite(SkillFxKind kind)
    {
        foreach (Vector3 aim in new[] { Vector3.Zero, new Vector3(float.NaN, 0f, 1f), new Vector3(float.PositiveInfinity, 0f, 0f) })
        {
            FxInstance[] instances = Cast(kind, out int count, aim, float.NaN);

            Assert.True(count - CountFrames(instances, count, FxFrame.Dust0, 4) is > 0 and <= FxSimulation.MaxCastParticles);
            for (int i = 0; i < count; i++)
            {
                Assert.True(float.IsFinite(instances[i].Position.X) && float.IsFinite(instances[i].Position.Y)
                    && float.IsFinite(instances[i].Position.Z) && float.IsFinite(instances[i].HalfSize)
                    && float.IsFinite(instances[i].Rotation) && float.IsFinite(instances[i].Stretch));
            }
        }

        FxSimulation none = new(1u);
        none.SpawnSkillCast(kind, new Vector3(float.NaN, 0f, 0f), Vector3.UnitX, 3f);
        Assert.Equal(0, none.ActiveParticles);
    }

    [Fact]
    public void A_Cast_Leaves_The_Reserve_Free_And_Counts_What_It_Declined()
    {
        FxSimulation simulation = new(6u);
        // The ambient dust tops itself up as time passes, so let it settle: the room below is what is left for the cast.
        FxFrameInputs settle = Inputs(0f);
        for (int i = 0; i < 12; i++)
        {
            simulation.Advance(in settle, []);
        }

        // Fill with Mend casts (their ring and motes outlive the sweep, 15 each) until 1 to 15 slots are open.
        while (FxLimits.ParticleCapacity - FxSimulation.CastPoolReserve - simulation.ActiveParticles > 15)
        {
            simulation.SpawnSkillCast(SkillFxKind.Mend, Origin, Vector3.Zero, 0f);
        }

        int room = FxLimits.ParticleCapacity - FxSimulation.CastPoolReserve - simulation.ActiveParticles;
        Assert.InRange(room, 1, 15);
        long droppedBefore = simulation.DroppedSpawns;
        int before = simulation.ActiveParticles;

        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        AdvanceTo(simulation, SweepDone);

        int added = simulation.ActiveParticles - before;
        Assert.Equal(room, added);
        Assert.Equal(19 - added, simulation.DroppedSpawns - droppedBefore);
        Assert.True(added < 19);
    }

    [Fact]
    public void Ambient_Dust_Still_Fits_After_Many_Casts_On_A_Nearly_Full_Pool()
    {
        FxSimulation simulation = new(8u);
        for (int i = 0; i < 400; i++)
        {
            simulation.SpawnSkillCast((SkillFxKind)(i % 3), Origin, Vector3.UnitX, 3.5f);
        }

        // Casts alone never get within the reserve of the capacity.
        Assert.True(simulation.ActiveParticles <= FxLimits.ParticleCapacity - FxSimulation.CastPoolReserve);
        FxFrameInputs inputs = Inputs(0f);
        for (int i = 0; i < 12; i++)
        {
            simulation.Advance(in inputs, []);
        }

        FxInstance[] instances = new FxInstance[FxLimits.MaxInstances];
        int count = simulation.WriteInstances(instances);
        Assert.Equal(FxSimulation.DustTarget, CountFrames(instances, count, FxFrame.Dust0, 4));
    }

    [Fact]
    public void The_Same_Seed_And_Cast_Give_Byte_Identical_Instances()
    {
        foreach (SkillFxKind kind in new[] { SkillFxKind.Arc, SkillFxKind.Bolt, SkillFxKind.Mend })
        {
            FxSimulation a = new(21u);
            FxSimulation b = new(21u);
            a.SpawnSkillCast(kind, Origin, Vector3.UnitZ, 3.5f);
            b.SpawnSkillCast(kind, Origin, Vector3.UnitZ, 3.5f);
            FxFrameInputs step = Inputs(0.05f);
            for (int i = 0; i < 12; i++)
            {
                a.Advance(in step, []);
                b.Advance(in step, []);
            }

            FxInstance[] ia = new FxInstance[FxLimits.MaxInstances];
            FxInstance[] ib = new FxInstance[FxLimits.MaxInstances];
            int ca = a.WriteInstances(ia);
            int cb = b.WriteInstances(ib);

            Assert.Equal(ca, cb);
            Assert.True(MemoryMarshal.AsBytes(ia.AsSpan(0, ca)).SequenceEqual(MemoryMarshal.AsBytes(ib.AsSpan(0, cb))));
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void No_Packed_Channel_Pins_At_255_At_Any_Time_Of_Day(float night)
    {
        foreach (SkillFxKind kind in new[] { SkillFxKind.Arc, SkillFxKind.Bolt, SkillFxKind.Mend })
        {
            FxSimulation simulation = new(5u);
            simulation.SpawnSkillCast(kind, Origin, Vector3.UnitX, 3.5f);
            FxFrameInputs step = Inputs(0.02f, night);
            FxInstance[] instances = new FxInstance[FxLimits.MaxInstances];
            for (int frame = 0; frame < 40; frame++)
            {
                simulation.Advance(in step, []);
                int count = simulation.WriteInstances(instances);
                for (int i = 0; i < count; i++)
                {
                    uint c = instances[i].Color;
                    // Dust is ambient; a cast's own sprites are what this gates.
                    if (instances[i].Frame >= (float)FxFrame.Dust0 && instances[i].Frame < (float)FxFrame.Ash0)
                    {
                        continue;
                    }

                    Assert.True((c & 0xFF) < 255 && ((c >> 8) & 0xFF) < 255 && ((c >> 16) & 0xFF) < 255, $"{kind} frame {frame} slot {i}");
                }
            }
        }
    }

    [Fact]
    public void Ground_Quads_Are_Pure_Light_At_Night_And_Occlude_By_Day()
    {
        FxInstance[] night = Cast(SkillFxKind.Mend, out _, night: 1f);
        FxInstance[] day = Cast(SkillFxKind.Mend, out _, night: 0f);
        // The ring starts fading in from zero, so age it a little first.
        FxSimulation simulation = new(3u);
        simulation.SpawnSkillCast(SkillFxKind.Mend, Origin, Vector3.Zero, 0f);
        FxFrameInputs dayStep = Inputs(0.1f, 0f);
        simulation.Advance(in dayStep, []);
        FxInstance[] aged = new FxInstance[FxLimits.MaxInstances];
        simulation.WriteInstances(aged);
        FxFrameInputs nightStep = Inputs(0f, 1f);
        simulation.Advance(in nightStep, []);
        FxInstance[] agedNight = new FxInstance[FxLimits.MaxInstances];
        simulation.WriteInstances(agedNight);

        Assert.Equal(0u, night[0].Color >> 24);
        Assert.Equal(0u, agedNight[0].Color >> 24);
        Assert.True((aged[0].Color >> 24) > 20u);
        Assert.Equal(0u, day[0].Color >> 24);
    }

    [Fact]
    public void The_Bridge_Forwards_A_Published_Cast_And_Stops_After_Dispose()
    {
        SceneEventHub events = new();
        FxSimulation simulation = new(1u);
        FxEventBridge bridge = new(events, simulation, new FxFlashList());

        events.Publish(new SkillCastEvent(SkillFxKind.Mend, Origin, Vector3.Zero, 0f));
        Assert.Equal(15, simulation.ActiveParticles);

        bridge.Dispose();
        events.Publish(new SkillCastEvent(SkillFxKind.Mend, Origin, Vector3.Zero, 0f));
        Assert.Equal(15, simulation.ActiveParticles);
    }

    [Fact]
    public void Casting_And_Stepping_Allocate_Nothing_On_A_Full_Pool()
    {
        FxSimulation simulation = new(17u);
        for (int i = 0; i < 200; i++)
        {
            simulation.SpawnDeath(Origin);
        }

        FxInstance[] destination = new FxInstance[FxLimits.MaxInstances];
        FxFrameInputs inputs = Inputs();

        AllocationAssert.AtMost(0, () =>
        {
            simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
            simulation.SpawnSkillCast(SkillFxKind.Bolt, Origin, Vector3.UnitX, 18f);
            simulation.SpawnSkillCast(SkillFxKind.Mend, Origin, Vector3.Zero, 0f);
            simulation.Advance(in inputs, []);
            if (simulation.WriteInstances(destination) < 0)
            {
                throw new InvalidOperationException();
            }
        });
        Assert.True(simulation.DroppedSpawns > 0);
    }

    [Fact]
    public void Casting_Allocates_Nothing_When_The_Casts_Actually_Spawn()
    {
        FxSimulation simulation = new(19u);
        FxInstance[] destination = new FxInstance[FxLimits.MaxInstances];
        // A long step so each frame's casts have expired by the next: every cast really spawns.
        FxFrameInputs inputs = Inputs(0.1f);

        AllocationAssert.AtMost(0, () =>
        {
            simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
            simulation.SpawnSkillCast(SkillFxKind.Bolt, Origin, Vector3.UnitZ, 18f);
            simulation.SpawnSkillCast(SkillFxKind.Mend, Origin, Vector3.Zero, 0f);
            for (int i = 0; i < 20; i++)
            {
                simulation.Advance(in inputs, []);
            }

            if (simulation.WriteInstances(destination) < 0)
            {
                throw new InvalidOperationException();
            }
        });
        Assert.Equal(0L, simulation.DroppedSpawns);
    }
}
