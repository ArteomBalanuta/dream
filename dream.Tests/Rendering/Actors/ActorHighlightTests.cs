using Dream.Rendering.Actors;
using Dream.Rendering.Ui;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Actors;

public sealed class ActorHighlightTests
{
    private static readonly Vector3 DireWolf = new(-0.45f, -0.45f, -0.45f);

    [Fact]
    public void TheLift_IsModestAndWarm()
    {
        Vector3 lift = ActorHighlight.Lift;
        Assert.True(lift.X > lift.Y && lift.Y > lift.Z && lift.Z > 0f);
        // A modest lift: no channel is raised by more than a third.
        Assert.True(lift.X <= 1f / 3f);
    }

    [Fact]
    public void TheRim_IsWarmBoneGold()
    {
        Vector3 rim = ActorHighlight.Rim;
        Assert.True(rim.X > rim.Y && rim.Y > rim.Z && rim.Z > 0f);
    }

    [Fact]
    public void RimFor_IsTheRimOnlyForTheHighlightedId()
    {
        Vector4 on = ActorHighlight.RimFor(7, 7);
        Assert.Equal(new Vector4(ActorHighlight.Rim, 0f), on);
        Assert.Equal(Vector4.Zero, ActorHighlight.RimFor(7, 8));
        Assert.Equal(Vector4.Zero, ActorHighlight.RimFor(null, 7));
    }

    [Fact]
    public void CombineOfZero_IsTheLift()
    {
        Vector3 combined = ActorHighlight.Combine(Vector3.Zero);
        Assert.Equal(ActorHighlight.Lift.X, combined.X, 5);
        Assert.Equal(ActorHighlight.Lift.Y, combined.Y, 5);
        Assert.Equal(ActorHighlight.Lift.Z, combined.Z, 5);
    }

    [Fact]
    public void ADarkTint_StaysDarkerThanThePlainHighlight()
    {
        Vector3 combined = ActorHighlight.Combine(DireWolf);

        Assert.True(combined.X < ActorHighlight.Lift.X && combined.Y < ActorHighlight.Lift.Y && combined.Z < ActorHighlight.Lift.Z);
        // Brighter than its own un-highlighted look, though.
        Assert.True(combined.X > DireWolf.X && combined.Y > DireWolf.Y && combined.Z > DireWolf.Z);
    }

    [Fact]
    public void BlackStaysBlack()
    {
        Vector3 black = ActorHighlight.Combine(new Vector3(-1f));

        Assert.Equal(new Vector3(-1f), black);
    }

    [Fact]
    public void EveryRarityBox_IsBrighterAndInRange()
    {
        foreach (ItemRarity rarity in Enum.GetValues<ItemRarity>())
        {
            Vector3 plain = WorldEntityRenderer.ItemColor(rarity);
            Vector3 lit = ActorHighlight.Box(plain);

            float ceiling = WorldEntityRenderer.AlbedoCeiling;
            Assert.True(MathF.Max(plain.X, MathF.Max(plain.Y, plain.Z)) <= ceiling, $"{rarity} box colour {plain} is over the albedo ceiling");
            Assert.True(lit.X > plain.X && lit.Y > plain.Y && lit.Z > plain.Z, rarity.ToString());
            Assert.True(MathF.Max(lit.X, MathF.Max(lit.Y, lit.Z)) <= (ceiling * (1f + ActorHighlight.Lift.X)) + 1e-6f, rarity.ToString());
        }
    }

    // The box lift is the same relative lift as an actor's (no clamp hides a too-bright base colour: the base
    // colours are what keep it in range, and EveryRarityBox pins them).
    [Fact]
    public void TheBoxLift_IsTheActorLift()
    {
        Vector3 colour = new(0.40f, 0.20f, 0.10f);
        Vector3 lit = ActorHighlight.Box(colour);

        Assert.Equal(colour * (Vector3.One + ActorHighlight.Lift), lit);
    }

    [Fact]
    public void TheEnemyTint_IsCombinedOnlyForTheHighlightedId()
    {
        Assert.Equal(ActorHighlight.Combine(DireWolf), ActorHighlight.EnemyTint(7, 7, DireWolf));
        Assert.Equal(DireWolf, ActorHighlight.EnemyTint(7, 8, DireWolf));
        Assert.Equal(DireWolf, ActorHighlight.EnemyTint(null, 7, DireWolf));
    }

    [Fact]
    public void TheNpcTint_IsTheLiftOnlyForTheHighlightedId()
    {
        Assert.Equal(ActorHighlight.Lift, ActorHighlight.NpcTint(3, 3));
        Assert.Equal(Vector3.Zero, ActorHighlight.NpcTint(3, 4));
        Assert.Equal(Vector3.Zero, ActorHighlight.NpcTint(null, 3));
    }

    // The corpse in the death linger is not hovered: the dying draw hands its stored plain tint to ApplyTint
    // and never asks ActorHighlight. Pinned on the source, since the GL class cannot be built here.
    [Fact]
    public void TheDyingDraw_KeepsThePlainTint()
    {
        string source = File.ReadAllText(RepoFile("dream", "Rendering", "Actors", "EnemyRenderer.cs"));
        int dying = source.IndexOf("private void DrawDyingModels", StringComparison.Ordinal);
        string dyingBody = source[dying..source.IndexOf("private Vector3 _appliedTint", StringComparison.Ordinal)];

        Assert.Contains("ApplyTint(model, shader, dying.Last.TintOffset, Vector4.Zero, camera.Position)", dyingBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ActorHighlight", dyingBody, StringComparison.Ordinal);
        Assert.Contains("ActorHighlight.EnemyTint(HighlightActorId, actorId, visual.TintOffset)", source, StringComparison.Ordinal);
        // And what the linger stores (the LastDrawnEnemy built for the death linger) is the plain tint.
        int stored = source.IndexOf("new EnemyDeathLinger.LastDrawnEnemy(", StringComparison.Ordinal);
        Assert.True(stored > 0);
        string storedArgs = source[stored..source.IndexOf(");", stored, StringComparison.Ordinal)];
        Assert.Contains("visual.TintOffset", storedArgs, StringComparison.Ordinal);
        Assert.DoesNotContain("ActorHighlight", storedArgs, StringComparison.Ordinal);
    }

    // The scene hands each renderer only the hovered thing of its own kind, and nothing for an id that is not an
    // actor id (actor ids are ints; a ground item keeps its long id).
    [Fact]
    public void Targets_LightOnlyTheHoveredKind()
    {
        Assert.Equal(new HighlightTargets(7, null, null), ActorHighlight.Targets(new HoverTargetId(HoverKind.Enemy, 7)));
        Assert.Equal(new HighlightTargets(null, 9, null), ActorHighlight.Targets(new HoverTargetId(HoverKind.Npc, 9)));
        Assert.Equal(new HighlightTargets(null, null, 5_000_000_000L), ActorHighlight.Targets(new HoverTargetId(HoverKind.GroundItem, 5_000_000_000L)));
        Assert.Equal(default, ActorHighlight.Targets(HoverTargetId.None));
        Assert.Equal(default, ActorHighlight.Targets(new HoverTargetId(HoverKind.RemotePlayer, 3)));
        Assert.Equal(default, ActorHighlight.Targets(new HoverTargetId(HoverKind.Enemy, 5_000_000_000L)));
    }

    // A lifted villager puts the shared uniform back to zero right after its own draw, in both programs and on
    // the model, or the lift reaches the next villager (and the MASK variant). Pinned on the source: GL class.
    [Fact]
    public void ALiftedVillager_ResetsTheTint_RightAfterItsDraw()
    {
        string source = File.ReadAllText(RepoFile("dream", "Rendering", "Actors", "NpcModelRenderer.cs")).Replace("\r\n", "\n");
        int end = source.IndexOf("model.EndRender();", StringComparison.Ordinal);
        Assert.True(end > 0);
        string after = source[end..source.IndexOf("_casters.Add(", end, StringComparison.Ordinal)];
        Assert.Contains("if (lifted)", after, StringComparison.Ordinal);
        Assert.Contains("ZeroTint(shader);", after, StringComparison.Ordinal);
        Assert.Contains("model.ActorTintOffset = Vector3.Zero;", after, StringComparison.Ordinal);
        int zero = source.IndexOf("void ZeroTint(", StringComparison.Ordinal);
        Assert.True(zero > 0, "ZeroTint must exist");
        string zeroBody = source[zero..source.IndexOf("\n    }", zero, StringComparison.Ordinal)];
        Assert.Contains("AlphaMaskVariant", zeroBody, StringComparison.Ordinal);
    }

    private static string RepoFile(params string[] parts)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "dream", "Rendering")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return Path.Combine([dir!, .. parts]);
    }
}
