using OpenTK.Mathematics;
using Xunit;
using Dream.Assets;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Rendering.Hud;

public class CombatTextPanelTests
{
    private static readonly UiLayout Layout1080 = UiLayout.Create(1920, 1080, 0f);

    private static UiSpriteTable LoadReal()
    {
        Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
        return table!;
    }

    private sealed class RecordingCanvas : IUiCanvas
    {
        public readonly record struct Glyph(UiSprite Sprite, UiRect Dst, UiColor Fill, UiColor Rim);

        public List<Glyph> Glyphs { get; } = new(256);

        public int OtherCalls { get; private set; }

        public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) => Glyphs.Add(new Glyph(s, dst, fill, rim));

        public void Sprite(in UiSprite s, UiRect dst, UiColor tint) => OtherCalls++;

        public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) => OtherCalls++;

        public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) => OtherCalls++;

        public void Tile(in UiSprite s, UiRect dst, UiColor tint) => OtherCalls++;

        public void Solid(UiRect dst, UiColor color) => OtherCalls++;

        public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color)
        {
            OtherCalls++;
            return 0f;
        }

        public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) => 0f;

        public void PushClip(UiRect r) => OtherCalls++;

        public void PopClip() => OtherCalls++;
    }

    private static FloatingCombatTextRenderState State(
        string text,
        FloatingCombatTextKind kind = FloatingCombatTextKind.Regular,
        float age = 0.5f,
        long order = 1,
        int target = 1,
        Vector2? anchor = null) =>
        new(
            text,
            Vector3.Zero,
            0f,
            0f,
            0f,
            1f,
            1f,
            FloatingCombatTextStyles.Resolve(kind),
            0,
            kind,
            age,
            order,
            target,
            1f,
            anchor ?? new Vector2(960f, 700f));

    private static RecordingCanvas Draw(UiSpriteTable table, params FloatingCombatTextRenderState[] states)
    {
        RecordingCanvas canvas = new();
        HudView view = new() { CombatText = states };
        CombatTextPanel.Build(canvas, view, Layout1080, table);
        return canvas;
    }

    [Fact]
    public void DrawsOneDigitSpritePerCharacter_CentredOnTheAnchor()
    {
        UiSpriteTable table = LoadReal();

        RecordingCanvas canvas = Draw(table, State("4087", anchor: new Vector2(960f, 700f)));

        Assert.Equal(4, canvas.Glyphs.Count);
        Assert.Equal(0, canvas.OtherCalls);
        Assert.Equal(
            [table.Get(UiSpriteNames.Digit(4)), table.Get(UiSpriteNames.Digit(0)), table.Get(UiSpriteNames.Digit(8)), table.Get(UiSpriteNames.Digit(7))],
            canvas.Glyphs.Select(g => g.Sprite));
        float left = canvas.Glyphs[0].Dst.X;
        float right = canvas.Glyphs[3].Dst.Right;
        Assert.InRange(((left + right) * 0.5f) - 960f, -4f, 4f);
        // Monospaced: the advance is 44/64 of the height.
        float height = canvas.Glyphs[0].Dst.H;
        Assert.Equal(height * 44f / 64f, canvas.Glyphs[1].Dst.X - canvas.Glyphs[0].Dst.X, 2);
    }

    [Theory]
    [InlineData(FloatingCombatTextKind.Regular, 0xFD, 0xB4, 0x42, 0x4A, 0x18, 0x00)]
    [InlineData(FloatingCombatTextKind.Critical, 0x0A, 0x9A, 0xFE, 0x00, 0x17, 0x50)]
    [InlineData(FloatingCombatTextKind.Incoming, 0xFF, 0x00, 0x00, 0x40, 0x00, 0x00)]
    public void ColoursFollowTheKind(FloatingCombatTextKind kind, int fr, int fg, int fb, int rr, int rg, int rb)
    {
        RecordingCanvas canvas = Draw(LoadReal(), State("12", kind));

        Assert.All(canvas.Glyphs, g =>
        {
            Assert.Equal(new UiColor((byte)fr, (byte)fg, (byte)fb, 255), g.Fill);
            Assert.Equal(new UiColor((byte)rr, (byte)rg, (byte)rb, 255), g.Rim);
        });
    }

    [Fact]
    public void NewestIsDrawnLast_AndAgedNumbersFade()
    {
        UiSpriteTable table = LoadReal();
        // The list order is not the spawn order.
        RecordingCanvas canvas = Draw(
            table,
            State("2", order: 9, age: 0.2f),
            State("1", order: 3, age: 1.2f),
            State("3", order: 5, age: 0.2f));

        Assert.Equal(
            [table.Get(UiSpriteNames.Digit(1)), table.Get(UiSpriteNames.Digit(3)), table.Get(UiSpriteNames.Digit(2))],
            canvas.Glyphs.Select(g => g.Sprite));
        Assert.Equal(255, canvas.Glyphs[2].Fill.A);
        Assert.InRange((int)canvas.Glyphs[0].Fill.A, 1, 254);
    }

    [Fact]
    public void ExpiredNumbersAndEmptyListsDrawNothing()
    {
        UiSpriteTable table = LoadReal();

        Assert.Empty(Draw(table).Glyphs);
        Assert.Empty(Draw(table, State("5", age: FloatingCombatTextMotion.ClassicLifetime + 0.1f)).Glyphs);
    }

    [Fact]
    public void ShrinksFromSpawnToTheFloor_AndCriticalsStartLarger()
    {
        UiSpriteTable table = LoadReal();

        float regularSpawn = Draw(table, State("1", age: 0f)).Glyphs[0].Dst.H;
        float criticalSpawn = Draw(table, State("1", FloatingCombatTextKind.Critical, age: 0f)).Glyphs[0].Dst.H;
        float settled = Draw(table, State("1", age: 0.4f)).Glyphs[0].Dst.H;

        Assert.Equal(68f * 0.85f, regularSpawn, 2);
        Assert.Equal(68f, criticalSpawn, 2);
        Assert.Equal(24f, settled, 2);
    }

    [Fact]
    public void ScalesWithScreenHeightAndSizeTuning()
    {
        UiSpriteTable table = LoadReal();
        RecordingCanvas canvas = new();
        HudView view = new() { CombatText = new[] { State("1", age: 0.4f) with { SizeFactor = 2f } } };

        CombatTextPanel.Build(canvas, view, UiLayout.Create(1280, 720, 0f), table);

        Assert.Equal(24f * (720f / 1080f) * 2f, canvas.Glyphs[0].Dst.H, 2);
    }

    [Fact]
    public void RisesWithAge()
    {
        UiSpriteTable table = LoadReal();

        float young = Draw(table, State("1", age: 0.2f)).Glyphs[0].Dst.Y;
        float older = Draw(table, State("1", age: 0.8f)).Glyphs[0].Dst.Y;

        Assert.True(older < young, "a numeral moves up the screen as it ages");
    }

    [Fact]
    public void OlderNumbersOfOneTarget_StayAtLeastTheirPitchAboveTheNewer()
    {
        UiSpriteTable table = LoadReal();
        // Same age, so they would overlap exactly without the guard.
        RecordingCanvas canvas = Draw(
            table,
            State("1", age: 0.5f, order: 1, target: 7),
            State("2", age: 0.5f, order: 2, target: 7),
            State("3", age: 0.5f, order: 3, target: 7));

        RecordingCanvas.Glyph oldest = canvas.Glyphs[0];
        RecordingCanvas.Glyph middle = canvas.Glyphs[1];
        RecordingCanvas.Glyph newest = canvas.Glyphs[2];
        Assert.Equal(middle.Dst.Bottom - (0.8f * oldest.Dst.H), oldest.Dst.Bottom, 2);
        Assert.Equal(newest.Dst.Bottom - (0.8f * middle.Dst.H), middle.Dst.Bottom, 2);
    }

    [Fact]
    public void DifferentTargetsDoNotPushEachOther()
    {
        RecordingCanvas canvas = Draw(
            LoadReal(),
            State("1", age: 0.5f, order: 1, target: 1),
            State("2", age: 0.5f, order: 2, target: 2));

        Assert.Equal(canvas.Glyphs[0].Dst.Bottom, canvas.Glyphs[1].Dst.Bottom, 2);
    }

    [Fact]
    public void IgnoresNonDigitsAndNeverFormats()
    {
        RecordingCanvas canvas = Draw(LoadReal(), State("-%d{0}9"));

        Assert.Equal(2, canvas.Glyphs.Count);
    }

    [Fact]
    public void BuildAllocatesNothingAfterWarmUp()
    {
        UiSpriteTable table = LoadReal();
        FloatingCombatTextRenderState[] states = new FloatingCombatTextRenderState[40];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = State((1000 + i).ToString(), i % 3 == 0 ? FloatingCombatTextKind.Critical : FloatingCombatTextKind.Regular, 0.1f + (i * 0.03f), i + 1, i % 4);
        }

        HudView view = new() { CombatText = states };
        NullCanvas canvas = new();
        CombatTextPanel.Build(canvas, view, Layout1080, table);

        long bytes = Dream.Tests.Utilities.AllocationAssert.MeasureBytesPerIteration(() =>
        {
            CombatTextPanel.Build(canvas, view, Layout1080, table);
        });

        Assert.Equal(0L, bytes);
        Assert.True(canvas.Glyphs > 0);
    }

    private sealed class NullCanvas : IUiCanvas
    {
        public int Glyphs { get; private set; }

        public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) => Glyphs++;

        public void Sprite(in UiSprite s, UiRect dst, UiColor tint) { }

        public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) { }

        public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) { }

        public void Tile(in UiSprite s, UiRect dst, UiColor tint) { }

        public void Solid(UiRect dst, UiColor color) { }

        public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color) => 0f;

        public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) => 0f;

        public void PushClip(UiRect r) { }

        public void PopClip() { }
    }
}

public class ClassicCombatTextStateTests
{
    private sealed class FixedAnchor(Vector3 anchor) : ICombatAnchorProvider
    {
        public Vector3 GetCombatAnchorWorldPosition() => anchor;
    }

    private static CombatActorHandle Actor(int id, CreatureKind kind = CreatureKind.Enemy) =>
        new(id, kind, new HealthState(100), new FixedAnchor(new Vector3(1f, 2f, 3f)));

    [Fact]
    public void RenderStatesCarryKindTargetAndSpawnOrder()
    {
        SceneEventHub hub = new();
        FloatingCombatTextSystem text = new();
        using CombatFeedbackSystem feedback = new(hub, text);

        hub.Publish(new DamageAppliedEvent(Actor(4), 10, FloatingCombatTextKind.Regular));
        hub.Publish(new DamageAppliedEvent(Actor(5), 20, FloatingCombatTextKind.Critical));
        List<FloatingCombatTextRenderState> states = new();
        text.CollectRenderStates(states);

        FloatingCombatTextRenderState regular = Assert.Single(states, s => s.Text == "10");
        FloatingCombatTextRenderState critical = Assert.Single(states, s => s.Text == "20");
        Assert.Equal((FloatingCombatTextKind.Regular, 4), (regular.Kind, regular.TargetId));
        Assert.Equal((FloatingCombatTextKind.Critical, 5), (critical.Kind, critical.TargetId));
        Assert.True(critical.SpawnOrder > regular.SpawnOrder);
        Assert.Equal(1f, regular.SizeFactor, 3);
    }

    [Fact]
    public void AgeIsOnTheClassicTimeline()
    {
        FloatingCombatTextSystem text = new();
        text.Spawn(1, Vector3.Zero, 5, FloatingCombatTextKind.Regular);
        text.Update(0.5f);
        List<FloatingCombatTextRenderState> states = new();

        text.CollectRenderStates(states);
        Assert.Equal(0.5f, Assert.Single(states).AgeSeconds, 3);

        FloatingCombatTextTuning faster = FloatingCombatTextTuning.Default with { MotionSpeedMultiplier = 1.2f, SizeMultiplier = 3.525f };
        text.CollectRenderStates(states, faster);
        Assert.Equal(1.0f, Assert.Single(states).AgeSeconds, 3);
        Assert.Equal(1.5f, states[0].SizeFactor, 3);
    }

    [Fact]
    public void AtTheCapTheTargetsOldestMakesRoom()
    {
        FloatingCombatTextSystem text = new();
        for (int i = 0; i < FloatingCombatTextSystem.MaxActivePerTarget + 3; i++)
        {
            text.Spawn(7, Vector3.Zero, 100 + i, FloatingCombatTextKind.Regular);
        }

        List<FloatingCombatTextRenderState> states = new();
        text.CollectRenderStates(states);

        Assert.Equal(12, states.Count);
        Assert.Contains(states, s => s.Text == "114");
        Assert.DoesNotContain(states, s => s.Text == "100");
        Assert.DoesNotContain(states, s => s.Text == "102");
        Assert.Contains(states, s => s.Text == "103");
    }

    [Fact]
    public void DamageOnTheLocalActorIsIncoming_OthersKeepTheirKind()
    {
        SceneEventHub hub = new();
        FloatingCombatTextSystem text = new();
        using CombatFeedbackSystem feedback = new(hub, text, () => 1);

        hub.Publish(new DamageAppliedEvent(Actor(1, CreatureKind.Player), 7, FloatingCombatTextKind.Regular));
        hub.Publish(new DamageAppliedEvent(Actor(2), 8, FloatingCombatTextKind.Critical));
        hub.Publish(new DamageAppliedEvent(Actor(3), 9, FloatingCombatTextKind.Regular));
        List<FloatingCombatTextRenderState> states = new();
        text.CollectRenderStates(states);

        Assert.Equal(FloatingCombatTextKind.Incoming, Assert.Single(states, s => s.Text == "7").Kind);
        Assert.Equal(FloatingCombatTextKind.Critical, Assert.Single(states, s => s.Text == "8").Kind);
        Assert.Equal(FloatingCombatTextKind.Regular, Assert.Single(states, s => s.Text == "9").Kind);
    }

    [Fact]
    public void ClassicCurve_HasTheDocumentedShape()
    {
        Assert.Equal(0f, FloatingCombatTextMotion.ClassicRisePixels(0f), 3);
        Assert.True(FloatingCombatTextMotion.ClassicRisePixels(0.5f) > FloatingCombatTextMotion.ClassicRisePixels(0.2f));
        Assert.Equal(3.4f * 85f, FloatingCombatTextMotion.ClassicRisePixels(1.4f), 2);
        Assert.Equal(1f, FloatingCombatTextMotion.ClassicAlpha(1.05f), 3);
        Assert.Equal(0.5f, FloatingCombatTextMotion.ClassicAlpha(1.225f), 3);
        Assert.Equal(0f, FloatingCombatTextMotion.ClassicAlpha(1.4f), 3);
    }
}
