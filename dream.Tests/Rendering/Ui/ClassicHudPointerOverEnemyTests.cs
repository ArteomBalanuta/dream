using OpenTK.Mathematics;
using Xunit;
using Dream.Assets;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;

namespace Dream.Tests.Rendering.Ui;

// The frame's PointerOverEnemy reaches the HudView through the real surface path.
public class ClassicHudPointerOverEnemyTests
{
    private sealed class NullCanvas : IUiCanvas
    {
        public void Sprite(in UiSprite s, UiRect dst, UiColor tint) { }
        public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) { }
        public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) { }
        public void Tile(in UiSprite s, UiRect dst, UiColor tint) { }
        public void Solid(UiRect dst, UiColor color) { }
        public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) { }
        public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color) => 0f;
        public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) => 0f;
        public void PushClip(UiRect r) { }
        public void PopClip() { }
    }

    private sealed class FakeBackend : IClassicHudBackend
    {
        public ClassicHudBackendState State { get; set; } = ClassicHudBackendState.Ready;
        public string? FailureReason { get; set; }
        public UiSpriteTable? Sprites { get; set; }
        public IUiCanvas BeginFrame(in UiLayout layout) => new NullCanvas();
        public void Submit() { }
        public void Dispose() { }
    }

    private static ClassicHudFrame Frame(bool overEnemy) => new()
    {
        Width = 1920,
        Height = 1080,
        ConfiguredScale = 0f,
        Pointer = new UiPointer(Vector2.Zero, false, false, false),
        PointerOverEnemy = overEnemy
    };

    [Fact]
    public void Draw_CarriesThePointerOverEnemyFlagToTheView_AndClearsIt()
    {
        Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
        ClassicHudSurface surface = new(new FakeBackend { Sprites = table });

        Assert.True(surface.Draw(Frame(true)));
        Assert.True(surface.View.PointerOverEnemy);

        Assert.True(surface.Draw(Frame(false)));
        Assert.False(surface.View.PointerOverEnemy);
    }
}
