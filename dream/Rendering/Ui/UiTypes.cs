using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

// Which atlas page a sprite lives on. Uncommon and Rare reuse the rects of ItemsCommon; only the
// texture differs. Font is the baked glyph page and has no JSON. Menu is its own atlas (see MenuUiRenderer).
public enum UiPage : byte
{
    Window,
    Hud,
    Cursors,
    ItemsCommon,
    ItemsUncommon,
    ItemsRare,
    Digits,
    Font,
    // The front-end art (title, buttons, panel). Loaded only by the menu renderer, never by the HUD atlas.
    Menu
}

// How a sprite repeats when it fills a larger rect. XY is in the window JSON (a tiled filler).
public enum UiTile : byte
{
    None,
    X,
    Y,
    XY
}

public readonly record struct UiColor(byte R, byte G, byte B, byte A)
{
    public static UiColor White => new(255, 255, 255, 255);

    public UiColor WithAlpha(byte alpha) => new(R, G, B, alpha);
}

// Screen rectangle in pixels, top-left origin. Contains is half-open: [X, X+W) x [Y, Y+H).
public readonly record struct UiRect(float X, float Y, float W, float H)
{
    public float Right => X + W;

    public float Bottom => Y + H;

    public bool Contains(Vector2 p) => p.X >= X && p.X < X + W && p.Y >= Y && p.Y < Y + H;
}

// One atlas sprite. X, Y, W, H are texels of the page and exclude the extrusion border. Slice
// fields are texels (all zero when the sprite has no slice). The footprint is the item art's cell
// size (1x1 when the JSON gives none).
public readonly record struct UiSprite(
    UiPage Page,
    int X,
    int Y,
    int W,
    int H,
    int PivotX,
    int PivotY,
    int SliceL,
    int SliceT,
    int SliceR,
    int SliceB,
    UiTile Tile,
    int FootprintCols,
    int FootprintRows)
{
    public bool HasSlice => SliceL != 0 || SliceT != 0 || SliceR != 0 || SliceB != 0;
}

public enum UiTextStyle : byte
{
    Body,
    Large
}
