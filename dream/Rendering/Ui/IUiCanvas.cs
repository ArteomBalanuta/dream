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

    // Multiplier on the baked glyph sizes (13 and 26 px). Panels size text boxes with it; a canvas
    // that never scales text leaves it at 1.
    float TextScale => 1f;

    // Quads the canvas can still take this frame. Optional panels (the world labels, which draw first
    // and so would take the room of everything above them) stop when it runs low.
    int QuadHeadroom => int.MaxValue;

    void PushClip(UiRect r);

    void PopClip();
}
