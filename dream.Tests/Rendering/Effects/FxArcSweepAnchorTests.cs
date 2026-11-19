using Dream.Rendering.Effects;
using Dream.Simulation.Combat;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// The server sweeps a Cleave from where the caster stands on each active tick, so the effect must be anchored
// to the caster's position when the swing turns active, not the position the cast was published with.
public class FxArcSweepAnchorTests
{
    private const int Caster = 7;
    private static readonly Vector3 Origin = new(10f, 1f, 4f);
    private static readonly FxViewBasis Basis = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
    private static readonly FxFrameInputs Step = new(1f / 64f, new Vector3(10f, 2f, 10f), -Vector3.UnitZ, new Vector3(10f, 2f, 10f), 0f, Basis);

    private sealed class Mover
    {
        public Vector3 Position = Origin;
        public bool Known = true;

        public bool Locate(int actorId, out Vector3 position)
        {
            position = Position;
            return Known && actorId == Caster;
        }
    }

    private static void Advance(FxSimulation simulation, float seconds)
    {
        int steps = (int)MathF.Round(seconds * 64f);
        for (int i = 0; i < steps; i++)
        {
            simulation.Advance(in Step, []);
        }
    }

    private static Vector3 QuadPosition(FxSimulation simulation)
    {
        FxInstance[] all = new FxInstance[FxLimits.MaxInstances];
        int count = simulation.WriteInstances(all);
        int quad = Array.FindIndex(all, 0, count, x => x.Frame == (float)FxFrame.Arc);
        Assert.True(quad >= 0);
        return all[quad].Position;
    }

    [Fact]
    public void A_Caster_Who_Moves_During_The_Windup_Gets_The_Crescent_At_The_Moved_Position()
    {
        Mover mover = new();
        FxSimulation simulation = new(3u) { CasterLocator = mover.Locate };
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f, Caster);

        int windupSteps = (int)MathF.Round(MeleeArcSwing.WindupSeconds * 64f);
        for (int i = 0; i < windupSteps; i++)
        {
            mover.Position = Origin + new Vector3(2f * (i + 1) / windupSteps, 0f, 0f);
            simulation.Advance(in Step, []);
        }

        Advance(simulation, 2f / 64f);
        Vector3 quad = QuadPosition(simulation);
        Assert.Equal(Origin.X + 2f, quad.X, 2);
        Assert.Equal(Origin.Z, quad.Z, 2);
    }

    [Fact]
    public void The_Crescent_Stays_Where_It_Was_Laid_While_The_Caster_Keeps_Moving()
    {
        Mover mover = new();
        FxSimulation simulation = new(3u) { CasterLocator = mover.Locate };
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f, Caster);
        Advance(simulation, MeleeArcSwing.WindupSeconds + 2f / 64f);
        Vector3 laid = QuadPosition(simulation);
        mover.Position += new Vector3(1.5f, 0f, 0f);
        Advance(simulation, 4f / 64f);
        Vector3 later = QuadPosition(simulation);
        Assert.Equal(laid.X, later.X, 1);
    }

    [Fact]
    public void An_Unknown_Caster_Falls_Back_To_The_Cast_Origin()
    {
        Mover mover = new() { Known = false };
        FxSimulation simulation = new(3u) { CasterLocator = mover.Locate };
        simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f, Caster);
        mover.Position += new Vector3(2f, 0f, 0f);
        Advance(simulation, MeleeArcSwing.WindupSeconds + 2f / 64f);
        Assert.Equal(Origin.X, QuadPosition(simulation).X, 2);

        // No locator at all, or no caster id: the same.
        FxSimulation bare = new(3u);
        bare.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f, Caster);
        Advance(bare, MeleeArcSwing.WindupSeconds + 2f / 64f);
        Assert.Equal(Origin.X, QuadPosition(bare).X, 2);

        Mover known = new();
        FxSimulation noId = new(3u) { CasterLocator = known.Locate };
        noId.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f);
        known.Position += new Vector3(2f, 0f, 0f);
        Advance(noId, MeleeArcSwing.WindupSeconds + 2f / 64f);
        Assert.Equal(Origin.X, QuadPosition(noId).X, 2);
    }

    [Fact]
    public void Following_A_Caster_Allocates_Nothing_In_Steady_State()
    {
        Mover mover = new();
        FxSimulation simulation = new(3u) { CasterLocator = mover.Locate };
        FxInstance[] destination = new FxInstance[FxLimits.MaxInstances];
        // Warm up so pools and JIT paths are settled.
        for (int i = 0; i < 4; i++)
        {
            simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f, Caster);
            Advance(simulation, MeleeArcSwing.TotalSeconds);
        }

        AllocationAssert.AtMost(0, () =>
        {
            simulation.SpawnSkillCast(SkillFxKind.Arc, Origin, Vector3.UnitX, 3.5f, Caster);
            mover.Position.X += 0.1f;
            simulation.Advance(in Step, []);
            if (simulation.WriteInstances(destination) < 0)
            {
                throw new InvalidOperationException();
            }
        });
    }
}
