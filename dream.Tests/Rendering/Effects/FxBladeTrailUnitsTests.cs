using Dream.Rendering.Actors;
using Dream.Rendering.Effects;
using Dream.Rendering.Models;
using Dream.Simulation.Combat;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// FxBladeTrailGeometry is in authored metres (model units over HeldItemScale.ModelUnitsPerMetre, what the clip test
// measures); the drawn figure is scaled by PlayerModelAssetDefinition.VisualScale. The effect must convert, and it
// must stand on the real feet whichever side published the cast.
public class FxBladeTrailUnitsTests
{
    private const int Caster = 7;
    private const float Tick = 1f / 64f;
    private static readonly Vector3 Body = new(10f, 3f, 4f);
    private static readonly FxViewBasis Basis = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
    private static readonly FxFrameInputs Step = new(Tick, new Vector3(10f, 2f, 10f), -Vector3.UnitZ, new Vector3(10f, 2f, 10f), 0f, Basis);

    private static float Feet => Body.Y - FxShadowRules.PlayerPoseToFeet;

    // Where the real render puts an authored-metre point of a figure facing +Z standing at Body, relative to its feet.
    private static Vector3 DrawnOffsetFromFeet(float horizontalAuthoredMetres, float heightAuthoredMetres)
    {
        PlayerRenderState state = PlayerRenderState.CreateLocal(Body, Vector3.UnitZ, isMoving: false);
        Vector3 units = new(0f, heightAuthoredMetres * HeldItemScale.ModelUnitsPerMetre, horizontalAuthoredMetres * HeldItemScale.ModelUnitsPerMetre);
        Vector3 world = Vector3.TransformPosition(units, state.ModelMatrix);
        return world - new Vector3(Body.X, Feet, Body.Z);
    }

    private static void Cast(float originLift, int ticks, out List<FxInstance> trails, out FxInstance crescent)
    {
        FxSimulation simulation = new(3u) { CasterLocator = Locate };
        simulation.SpawnSkillCast(SkillFxKind.Arc, Body + new Vector3(0f, originLift, 0f), Vector3.UnitZ, 3.5f, Caster);
        for (int i = 0; i < ticks; i++)
        {
            simulation.Advance(in Step, []);
        }

        FxInstance[] all = new FxInstance[FxLimits.MaxInstances];
        int count = simulation.WriteInstances(all);
        trails = [];
        crescent = default;
        for (int i = 0; i < count; i++)
        {
            if (all[i].Frame == (float)FxFrame.Trail)
            {
                trails.Add(all[i]);
            }
            else if (all[i].Frame == (float)FxFrame.Arc)
            {
                crescent = all[i];
            }
        }
    }

    private static bool Locate(int actorId, out Vector3 position)
    {
        position = Body;
        return actorId == Caster;
    }

    private static int Ticks(float seconds) => (int)MathF.Round(seconds * 64f);

    [Fact]
    public void The_Authored_To_World_Factor_Is_The_Scale_The_Player_Model_Is_Drawn_At()
    {
        // One authored metre is this many world metres in the real render matrix.
        Assert.Equal(DrawnOffsetFromFeet(0f, 1f).Y, FxSimulation.WorldMetresPerAuthoredMetre, 4);
        Assert.Equal(DrawnOffsetFromFeet(1f, 0f).Z, FxSimulation.WorldMetresPerAuthoredMetre, 4);
        Assert.True(FxSimulation.WorldMetresPerAuthoredMetre is > 0.3f and < 1f);
    }

    [Fact]
    public void The_Laid_Trail_Sits_Where_The_Drawn_Blade_Tip_Is()
    {
        Cast(0f, Ticks(MeleeArcSwing.ActiveEndSeconds), out List<FxInstance> trails, out _);
        Assert.Equal(FxSimulation.BladeTrailSegments, trails.Count);
        for (int i = 0; i < trails.Count; i++)
        {
            float p = (i + 0.5f) / FxSimulation.BladeTrailSegments;
            Vector3 drawn = DrawnOffsetFromFeet(FxBladeTrailGeometry.TipRadiusMetres, FxBladeTrailGeometry.TipHeightMetres(p));
            Vector3 offset = trails[i].Position - new Vector3(Body.X, Feet, Body.Z);
            Assert.Equal(drawn.Y, offset.Y, 0.01f);
            Assert.Equal(drawn.Z, MathF.Sqrt((offset.X * offset.X) + (offset.Z * offset.Z)), 0.01f);
        }
    }

    [Fact]
    public void A_Local_And_A_Remote_Cast_Of_The_Same_Body_Put_Trail_And_Crescent_At_The_Same_Height()
    {
        // Local casts are published from the body centre, remote ones from the eye (0.35 m higher).
        int ticks = Ticks(MeleeArcSwing.ActiveEndSeconds);
        Cast(0f, ticks, out List<FxInstance> local, out FxInstance localCrescent);
        Cast(0.35f, ticks, out List<FxInstance> remote, out FxInstance remoteCrescent);

        Assert.Equal(local.Count, remote.Count);
        for (int i = 0; i < local.Count; i++)
        {
            Assert.Equal(local[i].Position.Y, remote[i].Position.Y, 0.001f);
        }

        Assert.Equal(localCrescent.Position.Y, remoteCrescent.Position.Y, 0.001f);
        float lift = localCrescent.Position.Y - Feet;
        Assert.True(lift > 0f && lift < 0.1f, $"the crescent lies just above the feet, lift {lift}");
        float firstHeight = FxBladeTrailGeometry.TipHeightMetres(0.5f / FxSimulation.BladeTrailSegments) * FxSimulation.WorldMetresPerAuthoredMetre;
        Assert.Equal(Feet + firstHeight, local[0].Position.Y, 0.001f);
    }
}
