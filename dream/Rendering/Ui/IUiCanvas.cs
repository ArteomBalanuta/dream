using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

// Which gem a canvas draws: the vertex mode takes its extra sprites (empty, level map, glints) from the
// gem's own family in the sprite table.
public enum UiGemKind : byte
{
    Health,
    Mana,
}

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

    // A liquid gem: the sprite is the stone filled to the top (gem.<k>.full); the canvas finds the
    // empty glass, level map and glints beside it. fill is the level 0..1, phase the liquid surface
    // phase, pulse the low-health brightening 0..1 and sweep the glint sweep position. A canvas
    // without the mode draws the plain sprite, so the gem shows full.
    void Gem(in UiSprite full, UiRect dst, UiGemKind kind, float fill, float phase, float pulse, float sweep, UiColor tint)
        => Sprite(in full, dst, tint);

    // A sprite added to what is under it (light from the gems on the iron). A canvas without the mode
    // draws it normally.
    void Additive(in UiSprite s, UiRect dst, UiColor tint)
        => Sprite(in s, dst, tint);

    // Returns the advance width in pixels.
    float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color);

    float MeasureText(ReadOnlySpan<char> text, UiTextStyle style);

    // Multiplier on the baked glyph sizes (13 and 26 px). Panels size text boxes with it; a canvas
    // that never scales text leaves it at 1.
    float TextScale => 1f;

    // Sets the multiplier for the text that follows (a panel that sizes its own text restores it
    // after). A canvas that never scales text ignores it. UiBatch resets it from the layout each frame.
    void SetTextScale(float scale)
    {
    }

    // Quads the canvas can still take this frame. Optional panels (the world labels, which draw first
    // and so would take the room of everything above them) stop when it runs low.
    int QuadHeadroom => int.MaxValue;

    void PushClip(UiRect r);

    void PopClip();
}
