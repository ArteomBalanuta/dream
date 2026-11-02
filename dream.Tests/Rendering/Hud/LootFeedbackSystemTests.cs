using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Hud;

public sealed class LootFeedbackSystemTests
{
    [Fact]
    public void Lines_ReadAsLootedAndLost()
    {
        LootFeedbackSystem feed = new();

        feed.AddNotices(
        [
            new LootNotice("Leather Cap", ItemRarity.Rare, Lost: false),
            new LootNotice("Leather Cap", ItemRarity.Common, Lost: true)
        ]);

        Assert.Equal(2, feed.Count);
        Assert.Equal("Looted: Leather Cap (Rare)", feed.GetLine(0));
        Assert.Equal("Backpack full: Leather Cap dropped on the ground", feed.GetLine(1));
    }

    [Fact]
    public void ATrulyLostNotice_SaysWasLost_NotDroppedOnTheGround()
    {
        LootFeedbackSystem feed = new();

        feed.AddNotices([new LootNotice("Leather Cap", ItemRarity.Common, Lost: true, Gone: true)]);

        Assert.Equal("Backpack full: Leather Cap was lost", feed.GetLine(0));
    }

    [Fact]
    public void ItHoldsFourLines_ForFourSeconds()
    {
        LootFeedbackSystem feed = new();
        feed.AddNotices(Enumerable.Range(0, 6).Select(i => new LootNotice($"Item {i}", ItemRarity.Common, false)).ToArray());

        Assert.Equal(LootFeedbackSystem.MaxLines, feed.Count);
        Assert.Equal("Looted: Item 2 (Common)", feed.GetLine(0));
        Assert.Equal("Looted: Item 5 (Common)", feed.GetLine(3));

        feed.Update(3.9f);
        Assert.Equal(4, feed.Count);
        feed.Update(0.2f);
        Assert.Equal(0, feed.Count);
    }

    [Fact]
    public void AHostileList_IsCapped_AtEightNotices()
    {
        LootFeedbackSystem feed = new();
        // The first 32 are lost notices that would scroll the feed; only the first 8 are read, so the
        // newest line is notice 7.
        feed.AddNotices(Enumerable.Range(0, 40).Select(i => new LootNotice($"N{i}", ItemRarity.Common, true)).ToArray());

        Assert.Equal("Backpack full: N7 dropped on the ground", feed.GetLine(feed.Count - 1));
    }

    [Fact]
    public void HostileValues_NeverThrow()
    {
        LootFeedbackSystem feed = new();
        string huge = new('x', 100_000);

        Exception? thrown = Record.Exception(() =>
        {
            feed.AddNotices(null);
            feed.AddNotices(
            [
                new LootNotice(null!, (ItemRarity)200, false),
                new LootNotice(huge, ItemRarity.Common, false),
                new LootNotice("  ", ItemRarity.Common, true)
            ]);
            feed.Update(float.NaN);
            feed.Update(-5f);
        });

        Assert.Null(thrown);
        Assert.Equal("Looted: an item", feed.GetLine(0));
        Assert.True(feed.GetLine(1).Length < 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => feed.GetLine(9));
    }

    [Fact]
    public void ReadingAndUpdating_AllocateNothing()
    {
        LootFeedbackSystem feed = new();
        feed.AddNotices([new LootNotice("Cap", ItemRarity.Common, false), new LootNotice("Vest", ItemRarity.Rare, true)]);

        AllocationAssert.AtMost(0, () =>
        {
            feed.Update(0.001f);
            for (int i = 0; i < feed.Count; i++)
            {
                _ = feed.GetLine(i);
            }
        });
    }

    [Fact]
    public void HudDrawsTheFeedWithUnformattedText()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "dream", "Rendering", "Core", "Render.HudPanels.cs"));
        int start = source.IndexOf("private void DrawLootFeed", StringComparison.Ordinal);
        int end = source.IndexOf("private void DrawDeathOverlay", StringComparison.Ordinal);
        string body = source[start..end];

        Assert.Contains("ImGui.TextUnformatted(feed.GetLine(i))", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ImGui.Text(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TextColored", body, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "dream", "dream.csproj")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Repository root not found.");
    }
}
