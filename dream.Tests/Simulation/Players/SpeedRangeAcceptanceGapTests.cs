using System.Globalization;
using System.Reflection;
using Dream.Client.Scenes;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Players;
using Xunit;

namespace Dream.Tests.Simulation.Players;

// Acceptance gaps of the speed-range wave: the walk toggle's derived relation, the sheet's Attack Range
// following the main hand, and the wolf catching a cruising player but never a sprinting one.
public sealed class SpeedRangeAcceptanceGapTests
{
    private static float WalkAxisScale()
    {
        FieldInfo field = typeof(PlayScene).GetField("WalkAxisScale", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(field);
        return (float)field.GetRawConstantValue()!;
    }

    [Fact]
    public void TheWalkToggleScale_LandsUnderTheWalkFloor_AndAnUnscaledAxisDoesNot()
    {
        float scaled = WalkAxisScale() * PlayerSimulationTuning.DefaultRunSpeed;
        Assert.True(scaled < PlayerSimulationTuning.DefaultWalkSpeed, $"scaled cruise {scaled}");
        // Negative: an unscaled axis would cruise above the floor, so the relation can fail.
        Assert.False(1f * PlayerSimulationTuning.DefaultRunSpeed < PlayerSimulationTuning.DefaultWalkSpeed);
    }

    [Fact]
    public void TheSheetsAttackRange_FollowsTheMainHandItem()
    {
        HudStatusSnapshot Status(string? kind) =>
            new(50, 100, PlayerInteractionMode.Combat, 1, 2, 5, 1f, 5, 1, 0, 100) { AttackRange = WeaponReach.ForItemKind(kind) };
        string Row(HudStatusSnapshot s) => CharacterSheetModel.Build(s).Single(r => r.Label == "Attack Range").Value;

        string dagger = Row(Status("gear.iron-dagger"));
        string bow = Row(Status("gear.long-bow"));
        string bare = Row(Status(null));

        Assert.Equal("1.5 m", dagger);
        Assert.Equal("14.0 m", bow);
        Assert.Equal(WeaponReach.UnarmedMetres.ToString("0.0#", CultureInfo.InvariantCulture) + " m", bare);
        Assert.NotEqual(dagger, bow);
        Assert.NotEqual(dagger, bare);
    }

    [Fact]
    public void TheSheetsSpeedRows_ShowTheShippedNumbers()
    {
        HudStatusSnapshot status = new(50, 100, PlayerInteractionMode.Combat, 1, 2, 5, 1f, 5, 1, 0, 100);
        CharacterSheetRow[] rows = CharacterSheetModel.Build(status);
        Assert.Equal("1.72 m/s", rows.Single(r => r.Label == "Move Speed").Value);
        Assert.Equal("4.00 m/s", rows.Single(r => r.Label == "Run Speed").Value);
    }

    // Closes a gap by fixed ticks: pursuer starts 8 m behind, 20 s of chase at the shipped speeds.
    private static float GapAfter(float pursuerSpeed, float targetSpeed)
    {
        const float Dt = 1f / 60f;
        float gap = 8f;
        for (int i = 0; i < 20 * 60; i++)
        {
            gap = MathF.Max(0f, gap - ((pursuerSpeed - targetSpeed) * Dt));
        }

        return gap;
    }

    [Fact]
    public void EveryShippedWolf_CatchesACruisingPlayer_AndNeverASprintingOne()
    {
        Assert.All(ContentCatalog.Shipped.Enemies, template =>
        {
            Assert.Equal(0f, GapAfter(template.MoveSpeed, PlayerSimulationTuning.DefaultRunSpeed));
            Assert.True(
                GapAfter(template.MoveSpeed, PlayerSimulationTuning.DefaultSprintSpeed) >= 8f,
                $"{template.Key} must not close on a sprinting player");
        });
        // Negative: a wolf at the cruise speed never closes.
        Assert.Equal(8f, GapAfter(PlayerSimulationTuning.DefaultRunSpeed, PlayerSimulationTuning.DefaultRunSpeed));
    }
}
