using System.IO;
using Xunit;

namespace Dream.Tests.Rendering.Actors;

// GL cannot open in a test, so the villagers' draw is verified in a capture (M5). These pin the
// wiring that capture depends on: the renderer runs once a frame from the world-entity pass, with
// that frame's dt, and the boxes it replaced are gone. Same shape as
// EnemyVisualCatalogTests.TheEnemyRenderer_ResetsTheTintAfterTheEnemies.
public sealed class NpcModelRendererWiringTests
{
    [Fact]
    public void TheWorldEntityPass_DrawsTheVillagerModels_WithTheFramesDelta()
    {
        string source = ReadRepoFile("dream", "Rendering", "Core", "Render.GroundItems.cs");

        Assert.Contains("Npcs.Draw(camera, npcs, deltaTime);", source, StringComparison.Ordinal);
        Assert.Contains("float deltaTime)", source, StringComparison.Ordinal);
        // Before the boxes, so a villager draws even in a frame with no ground items (the box pass
        // returns early when its list is empty).
        Assert.True(
            source.IndexOf("Npcs.Draw(", StringComparison.Ordinal) <
            source.IndexOf("WorldEntityRenderer.CollectBoxes", StringComparison.Ordinal));
    }

    [Fact]
    public void TheVillagersBoxes_AreGoneFromTheWorldEntityRenderer()
    {
        string source = ReadRepoFile("dream", "Rendering", "Actors", "WorldEntityRenderer.cs");

        Assert.DoesNotContain("RobeColor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HeadColor", source, StringComparison.Ordinal);
        // The labels still hang above the villager's head.
        Assert.Contains("RobeSize.Y + HeadSize.Y", source, StringComparison.Ordinal);
    }

    // GL cannot open in a test either, so this pins the source: the villagers must not depend on
    // EnemyRenderer having reset the shared uActorTintOffset before they draw. The upload sits
    // before the per-NPC loop, so it happens once a frame whatever the enemy pass did.
    [Fact]
    public void TheVillagers_ZeroTheSharedTint_Themselves_BeforeTheLoop()
    {
        string source = ReadRepoFile("dream", "Rendering", "Actors", "NpcModelRenderer.cs");

        int upload = source.IndexOf("shader.SetVector3(TintUniformName, Vector3.Zero);", StringComparison.Ordinal);
        Assert.True(upload > 0, "NpcModelRenderer no longer uploads a zero tint of its own.");
        Assert.True(
            upload < source.IndexOf("for (int n = 0; n < npcs.Count; n++)", StringComparison.Ordinal),
            "The zero tint must be uploaded once before the per-NPC loop.");
        // The MASK variant is covered too, as EnemyRenderer.ResetTint does.
        Assert.Contains("maskProgram.SetVector3(TintUniformName, Vector3.Zero)", source, StringComparison.Ordinal);
        Assert.Equal("uActorTintOffset", Dream.Rendering.Actors.NpcModelRenderer.TintUniformName);
    }

    [Fact]
    public void TheForgedActorPaths_ArePreloaded()
    {
        string source = ReadRepoFile("dream", "Client", "Scenes", "PlayScene.Lifetime.cs");

        Assert.Contains("ModelAssets.HoundPath", source, StringComparison.Ordinal);
        Assert.Contains("ModelAssets.NpcVendorPath", source, StringComparison.Ordinal);
        Assert.Contains("ModelAssets.NpcSmithPath", source, StringComparison.Ordinal);
        Assert.Contains("ModelAssets.NpcWardenPath", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "dream", "Rendering")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        return File.ReadAllText(Path.Combine(new[] { root! }.Concat(parts).ToArray()));
    }
}
