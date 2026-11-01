using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

// What panels draw with. Everything is screen pixels, top-left origin. Text takes spans and never
// treats its input as a format string.
public interface IUiCanvas
{
    void Sprite(in UiSprite s, UiRect dst, UiColor tint);

    // Draws the part of the sprite inside srcNormalized (0..1 of the sprite) into dst.
    void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint);

    void NineSlice(in UiSprite s, UiRect dst, UiColor tint);

    void Tile(in UiSprite s, UiRect dst, UiColor tint);

    void Solid(UiRect dst, UiColor color);

    // Two-channel mask sprite: fill from the R channel, rim from G.
    void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim);

    // Returns the advance width in pixels.
    float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color);

    float MeasureText(ReadOnlySpan<char> text, UiTextStyle style);

    void PushClip(UiRect r);

    void PopClip();
}
