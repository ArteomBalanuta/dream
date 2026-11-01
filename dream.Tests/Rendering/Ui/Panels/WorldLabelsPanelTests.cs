using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

public class WorldLabelsPanelTests
{
    private static UiLayout Layout() => UiLayout.Create(1920, 1080, 0f);

    private static HudView ViewWith(params HudWorldLabel[] labels)
    {
        HudView v = StatusBarTestData.View();
        v.Labels = labels;
        return v;
    }

    private static StatusBarRecordingCanvas Run(HudView v, bool recording = true)
    {
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table, recording);
        WorldLabelsPanel.Build(c, v, Layout());
        return c;
    }

    [Fact]
    public void Label_DrawsNameThenHealthBelowItAtTheAnchor()
    {
        StatusBarRecordingCanvas c = Run(ViewWith(new HudWorldLabel(new Vector2(500f, 300f), "Slime Lv 3", "HP 4/10")));

        StatusBarCall name = c.Texts.Last(t => t.Text == "Slime Lv 3");
        StatusBarCall health = c.Texts.Last(t => t.Text == "HP 4/10");
        Assert.Equal(300f, name.TextPos.Y);
        Assert.True(health.TextPos.Y > name.TextPos.Y);
        Assert.Equal(500f, name.TextPos.X + "Slime Lv 3".Length * 7f * 0.5f, 0);
        Assert.Empty(c.Sprites("hud.slab.normal"));
        Assert.DoesNotContain(c.Calls, x => x.Kind != StatusBarCallKind.Text);
    }

    [Fact]
    public void PercentAndBracesStayLiteralAndLongNamesAreClipped()
    {
        string hostile = "%s{0}" + new string('x', 500);
        StatusBarRecordingCanvas c = Run(ViewWith(new HudWorldLabel(new Vector2(500f, 300f), hostile, "")));

        string drawn = c.Texts.Last().Text!;
        Assert.StartsWith("%s{0}xxx", drawn);
        Assert.True(drawn.Length * 7f <= WorldLabelsPanel.MaxWidthUnits * 2.25f);
        Assert.True(drawn.Length < hostile.Length);
        Assert.All(c.Texts, t => Assert.Equal(drawn, t.Text));
    }

    [Fact]
    public void OffScreenAndNonFiniteLabelsAreSkipped()
    {
        StatusBarRecordingCanvas c = Run(ViewWith(
            new HudWorldLabel(new Vector2(-5000f, 100f), "Far left", "x"),
            new HudWorldLabel(new Vector2(float.NaN, 100f), "NaN", "x"),
            new HudWorldLabel(new Vector2(100f, 5000f), "Below", "x")));

        Assert.Empty(c.Calls);
    }

    [Fact]
    public void EmptyNameAndHealthDrawNothing()
    {
        Assert.Empty(Run(ViewWith(new HudWorldLabel(new Vector2(500f, 300f), "", ""))).Calls);
    }

    [Fact]
    public void Build_AllocatesNothingAfterWarmUp()
    {
        HudView v = ViewWith(new HudWorldLabel(new Vector2(500f, 300f), "Slime", "HP 4/10"), new HudWorldLabel(new Vector2(900f, 400f), "Wolf", "HP 9/9"));
        UiLayout l = Layout();
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table, recording: false);

        for (int i = 0; i < 3; i++)
        {
            WorldLabelsPanel.Build(c, v, l);
        }

        long bytes = Dream.Tests.Utilities.AllocationAssert.MeasureBytesPerIteration(() =>
        {
            WorldLabelsPanel.Build(c, v, l);
        });

        Assert.Equal(0L, bytes);
        Assert.True(c.Count > 10);
    }
}
