using Dream.Rendering.Actors;
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
        Assert.True(lift.X <= 0.40f);
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

            Assert.True(lit.X > plain.X && lit.Y > plain.Y && lit.Z > plain.Z, rarity.ToString());
            Assert.True(lit.X <= 1f && lit.Y <= 1f && lit.Z <= 1f, rarity.ToString());
        }
    }

    [Fact]
    public void ABoxChannelNearOne_IsClamped()
    {
        Vector3 lit = ActorHighlight.Box(new Vector3(0.95f, 0.2f, 0.2f));

        Assert.Equal(1f, lit.X);
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

        Assert.Contains("ApplyTint(model, shader, dying.Last.TintOffset)", dyingBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ActorHighlight", dyingBody, StringComparison.Ordinal);
        Assert.Contains("ActorHighlight.EnemyTint(HighlightActorId, actorId, visual.TintOffset)", source, StringComparison.Ordinal);
        // And what the linger stores is the plain tint.
        Assert.Contains("visual.TintOffset,\n", source.Replace("\r\n", "\n"), StringComparison.Ordinal);
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
