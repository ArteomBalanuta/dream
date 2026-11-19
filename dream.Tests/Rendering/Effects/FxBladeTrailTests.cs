using Dream.Rendering.Effects;
using Dream.Simulation.Combat;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// The Cleave blade trail: white, additive billboard segments laid along FxBladeTrailGeometry's path as the active
// window sweeps, each fading from its own birth. Nothing here reads the atlas PNG.
public class FxBladeTrailTests
{
    private const float Tick = 1f / 64f;
    private static readonly Vector3 Origin = new(10f, 1f, 4f);
    private static readonly Vector3 Camera = new(10f, 2f, 10f);
    private static readonly FxViewBasis FlatBasis = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    private static readonly Vector3 Body = new(10f, 0.5f, 4f);

    private static bool LocateBody(int actorId, out Vector3 position)
    {
        position = Body;
        return actorId == 5;
    }

    private static FxFrameInputs Inputs(float dt, float night = 0f, FxViewBasis? basis = null) =>
        new(dt, Camera, -Vector3.UnitZ, Camera, night, basis ?? FlatBasis);

    private static void Step(FxSimulation simulation, int ticks, float night = 0f, FxViewBasis? basis = null)
    {
        FxFrameInputs step = Inputs(Tick, night, basis);
        for (int i = 0; i < ticks; i++)
        {
            simulation.Advance(in step, []);
        }
    }

    private static int TicksFor(float seconds) => (int)MathF.Round(seconds * 64f);

    private static readonly int WindupTicks = TicksFor(MeleeArcSwing.WindupSeconds);
    private static readonly int ActiveTicks = TicksFor(MeleeArcSwing.ActiveSeconds);

    private static FxSimulation CastAt(int ticks, Vector3? aim = null, uint seed = 3u, FxViewBasis? basis = null)
    {
        FxSimulation simulation = new(seed);
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, aim ?? Vector3.UnitX, 3.5f);
        Step(simulation, ticks, 0f, basis);
        return simulation;
    }

    // The trail instances in pool order (emission order, while nothing has been removed). A zero-length step puts the
    // frame inputs (night factor, basis) in without ageing anything.
    private static List<FxInstance> Trails(FxSimulation simulation, float night = 0f, FxViewBasis? basis = null)
    {
        FxFrameInputs hold = Inputs(0f, night, basis);
        simulation.Advance(in hold, []);
        FxInstance[] all = new FxInstance[FxLimits.MaxInstances];
        int count = simulation.WriteInstances(all);
        List<FxInstance> trails = [];
        for (int i = 0; i < count; i++)
        {
            if (all[i].Frame == (float)FxFrame.Trail)
            {
                trails.Add(all[i]);
            }
        }

        return trails;
    }

    private static float Channel(uint packed, int shift) => ((packed >> shift) & 0xFFu) / 255f;

    [Fact]
    public void No_Trail_Before_The_Swing_Turns_Active()
    {
        FxSimulation simulation = CastAt(WindupTicks - 1);
        Assert.Empty(Trails(simulation));
    }

    [Fact]
    public void One_Tick_Into_The_Window_The_First_Segment_Sits_On_The_Left_End_Of_The_Path()
    {
        // Located at a body centre; the effect stands on the feet below it, whatever origin the cast was published from.
        FxSimulation simulation = new(3u) { CasterLocator = LocateBody };
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f, 5);
        Step(simulation, WindupTicks + 1);
        List<FxInstance> trails = Trails(simulation);

        Assert.NotEmpty(trails);
        Vector3 offset = trails[0].Position - Body;
        // Aim +X, so the caster's right is +Z.
        float theta = MathF.Atan2(offset.Z, offset.X);
        float expected = FxBladeTrailGeometry.TipAngleRadians(0.5f / FxSimulation.BladeTrailSegments);
        Assert.Equal(expected, theta, 3);
        Assert.True(theta < 0f, "the first segment is on the caster's left");
        // The geometry is authored metres; the world sizes follow the drawn figure's scale.
        float scale = FxSimulation.WorldMetresPerAuthoredMetre;
        Assert.Equal(FxBladeTrailGeometry.TipRadiusMetres * scale, MathF.Sqrt((offset.X * offset.X) + (offset.Z * offset.Z)), 0.05f);
        float feetY = Body.Y - FxShadowRules.PlayerPoseToFeet;
        Assert.Equal(FxBladeTrailGeometry.TipHeightMetres(0.5f / FxSimulation.BladeTrailSegments) * scale, trails[0].Position.Y - feetY, 0.01f);
    }

    [Fact]
    public void By_The_End_Of_The_Window_All_Segments_Exist_Ordered_Left_To_Right()
    {
        FxSimulation simulation = CastAt(WindupTicks + ActiveTicks);
        List<FxInstance> trails = Trails(simulation);

        Assert.Equal(FxSimulation.BladeTrailSegments, trails.Count);
        for (int i = 1; i < trails.Count; i++)
        {
            Assert.True(trails[i].Position.Z > trails[i - 1].Position.Z, $"segment {i} is not right of segment {i - 1}");
        }
    }

    [Fact]
    public void Segments_Are_Bright_Additive_Camera_Facing_Billboards_At_Birth()
    {
        FxSimulation simulation = new(3u);
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        Step(simulation, WindupTicks - 1);
        int seen = 0;
        for (int tick = 0; tick <= ActiveTicks + 1; tick++)
        {
            Step(simulation, 1);
            List<FxInstance> trails = Trails(simulation);
            for (; seen < trails.Count; seen++)
            {
                FxInstance s = trails[seen];
                Assert.True(s.Stretch >= 1f && s.Stretch != FxInstance.GroundAligned, "a trail segment is a billboard");
                Assert.True(s.DepthBias > 0f);
                Assert.Equal(0u, s.Color >> 24);
                float r = Channel(s.Color, 0);
                float g = Channel(s.Color, 8);
                float b = Channel(s.Color, 16);
                foreach (float c in new[] { r, g, b })
                {
                    Assert.True(c * 255f >= 200f && c * 255f < 255f, $"channel byte {c * 255f}");
                }

                Assert.True(MathF.Min(r, MathF.Min(g, b)) / MathF.Max(r, MathF.Max(g, b)) >= 0.9f);
            }
        }

        Assert.Equal(FxSimulation.BladeTrailSegments, seen);
    }

    [Fact]
    public void The_Oldest_Segment_Fades_And_Thins_First_And_None_Remain_After_Their_Life()
    {
        FxSimulation simulation = CastAt(WindupTicks + ActiveTicks + 1);
        List<FxInstance> trails = Trails(simulation);
        Assert.Equal(FxSimulation.BladeTrailSegments, trails.Count);
        FxInstance oldest = trails[0];
        FxInstance newest = trails[^1];
        Assert.True(Channel(oldest.Color, 0) < Channel(newest.Color, 0));
        Assert.True(oldest.HalfSize < newest.HalfSize);

        Step(simulation, TicksFor(FxSimulation.TrailLife) + 1);
        Assert.Empty(Trails(simulation));
    }

    public static IEnumerable<object[]> Views()
    {
        foreach (float yaw in new[] { 0f, 90f, 180f, 270f })
        {
            yield return [IsometricBasis(50f, yaw)];
        }

        // Straight down, screen-up toward -Z.
        yield return [new FxViewBasis(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY)];
    }

    // Camera right/up/back for a camera pitched down by `pitchDegrees`, looking along `yawDegrees` about +Y.
    private static FxViewBasis IsometricBasis(float pitchDegrees, float yawDegrees)
    {
        float pitch = MathHelper.DegreesToRadians(pitchDegrees);
        float yaw = MathHelper.DegreesToRadians(yawDegrees);
        Vector3 forward = new(MathF.Sin(yaw) * MathF.Cos(pitch), -MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Cross(right, forward);
        return new FxViewBasis(right, up, -forward);
    }

    private static float PlateauHalf(FxInstance s) => s.HalfSize * s.Stretch * FxBladeTrailGeometry.CellPlateauFraction;

    [Theory]
    [MemberData(nameof(Views))]
    public void Consecutive_Segments_Leave_No_Gap_On_Screen(FxViewBasis basis)
    {
        foreach (Vector3 aim in new[] { Vector3.UnitX, -Vector3.UnitZ })
        {
            FxSimulation simulation = CastAt(WindupTicks + ActiveTicks, aim, basis: basis);
            List<FxInstance> trails = Trails(simulation, basis: basis);
            Assert.Equal(FxSimulation.BladeTrailSegments, trails.Count);

            for (int i = 0; i + 1 < trails.Count; i++)
            {
                Vector3 d = trails[i + 1].Position - trails[i].Position;
                float distance = MathF.Sqrt(MathF.Pow(Vector3.Dot(d, basis.Right), 2f) + MathF.Pow(Vector3.Dot(d, basis.Up), 2f));
                float reach = PlateauHalf(trails[i]) + PlateauHalf(trails[i + 1]);
                Assert.True(distance <= reach, $"gap between segments {i} and {i + 1}: {distance} > {reach}");
            }
        }
    }

    [Fact]
    public void An_Unaimed_Cast_Lays_No_Trail()
    {
        FxSimulation simulation = new(3u);
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.Zero, 3.5f);
        Step(simulation, TicksFor(MeleeArcSwing.TotalSeconds));
        Assert.Empty(Trails(simulation));
        Assert.Equal(0L, simulation.DroppedSpawns);
    }

    [Fact]
    public void A_Nearly_Full_Pool_Keeps_The_Reserve_And_Counts_The_Segments_It_Declined()
    {
        FxSimulation simulation = new(6u);
        Step(simulation, 12);
        while (FxLimits.ParticleCapacity - FxSimulation.CastPoolReserve - simulation.ActiveParticles > 15)
        {
            simulation.SpawnSkillCast(SkillFxKind.Mend, Origin, Vector3.Zero, 0f);
        }

        int room = FxLimits.ParticleCapacity - FxSimulation.CastPoolReserve - simulation.ActiveParticles;
        Assert.InRange(room, 1, 15);
        long droppedBefore = simulation.DroppedSpawns;
        int before = simulation.ActiveParticles;

        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        Step(simulation, WindupTicks + ActiveTicks + 1);

        int added = simulation.ActiveParticles - before;
        Assert.Equal(room, added);
        Assert.Equal(FxSimulation.MaxCastParticles - added, simulation.DroppedSpawns - droppedBefore);
        Assert.True(simulation.ActiveParticles <= FxLimits.ParticleCapacity - FxSimulation.CastPoolReserve);
    }

    [Fact]
    public void Sweeps_In_Flight_Allocate_Nothing_In_Steady_State()
    {
        FxSimulation simulation = new(3u);
        FxInstance[] destination = new FxInstance[FxLimits.MaxInstances];
        FxFrameInputs step = Inputs(Tick);
        for (int i = 0; i < 4; i++)
        {
            simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
            Step(simulation, TicksFor(MeleeArcSwing.TotalSeconds));
        }

        AllocationAssert.AtMost(0, () =>
        {
            simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
            for (int i = 0; i < WindupTicks + ActiveTicks + 4; i++)
            {
                simulation.Advance(in step, []);
                if (simulation.WriteInstances(destination) < 0)
                {
                    throw new InvalidOperationException();
                }
            }
        });
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void No_Packed_Channel_Reaches_255_At_Any_Time_Of_Day(float night)
    {
        FxSimulation simulation = new(3u);
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        for (int tick = 0; tick < TicksFor(MeleeArcSwing.TotalSeconds) + 4; tick++)
        {
            Step(simulation, 1, night);
            foreach (FxInstance s in Trails(simulation, night))
            {
                Assert.True((s.Color & 0xFFu) < 255u && ((s.Color >> 8) & 0xFFu) < 255u && ((s.Color >> 16) & 0xFFu) < 255u);
            }
        }
    }
}
