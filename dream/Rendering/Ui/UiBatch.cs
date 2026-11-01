using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

// How the fragment stage reads a vertex. The mode is the low byte of UiVertex.Mode; a Gem vertex also
// carries which gem it draws and which art generation its mask comes from in the bits above it.
public static class UiVertexMode
{
    // Texture colour times tint, premultiplied.
    public const uint Color = 0;

    // Two-channel mask: rgb = (tex.r * tint + tex.g * rim) with the tint/rim alphas as fades.
    public const uint Mask = 1;

    // Flat colour, no texture sample.
    public const uint Solid = 2;

    // A liquid gem. The quad's UVs cover gem.<k>.full; the fragment stage reads the empty glass, the level
    // map and the glints from the same page at a fixed UV offset each (UiBatch.GemOffsets). Rim bytes:
    // R fill 0..1, G surface phase, B low-health pulse, A glint sweep. Tint is the colour and fade.
    public const uint Gem = 3;

    // Texture colour times tint added to what is below: output (rgb * a, 0) in the premultiplied blend.
    public const uint Additive = 4;

    // The mode is the low byte of the Mode word.
    public const uint BaseMask = 0xFF;

    // Gem vertices: bit 8 is the gem kind (0 health, 1 mana).
    public const int GemKindShift = 8;

    // Gem vertices: bit 9 is set when the mask sprite is the old top-down gradient (R 0 at the top of
    // the stone, 1 at the bottom, G and B unused). Clear means the baked level map.
    public const uint GemLegacyMask = 1u << 9;

    public static uint Base(uint mode) => mode & BaseMask;
}

// 28 bytes. Colours are R,G,B,A in memory order (R in the low byte).
[StructLayout(LayoutKind.Sequential)]
public struct UiVertex
{
    public float X;
    public float Y;
    public float U;
    public float V;
    public uint Tint;
    public uint Rim;
    public uint Mode;
}

// Scissor rectangle in screen pixels, top-left origin, half-open [X0,X1) x [Y0,Y1).
public readonly record struct UiClipRect(int X0, int Y0, int X1, int Y1)
{
    public bool IsEmpty => X1 <= X0 || Y1 <= Y0;
}

// A run of quads drawn with one texture and one scissor. Solid-only runs have no texture of their
// own (PageFree) and adopt the page of the next textured quad.
public struct UiDrawCommand
{
    public UiPage Page;
    public UiClipRect Clip;
    public int FirstIndex;
    public int IndexCount;
    public bool PageFree;
}

// CPU side of the classic HUD canvas. Panels draw into it through IUiCanvas; it writes quads into
// preallocated arrays and merges them into draw commands (same texture and scissor, in draw order).
// No GL, no allocation after construction. Quads past the capacity are dropped and counted.
//
// Per frame: BeginFrame, build, then hand Vertices/Commands to the GL layer (or the test
// compositor). Vertex positions are screen pixels with a top-left origin; UVs have a top-left origin
// too (images are uploaded unflipped), so there is no V flip anywhere.
public sealed class UiBatch : IUiCanvas
{
    public const int MaxQuads = 8192;
    public const int MaxCommands = 256;
    public const int MaxClipDepth = 16;
    public const int GemKindCount = 2;

    private static readonly int PageCount = Enum.GetValues<UiPage>().Length;

    private readonly UiVertex[] _vertices = new UiVertex[MaxQuads * 4];
    private readonly UiDrawCommand[] _commands = new UiDrawCommand[MaxCommands];
    private readonly UiClipRect[] _clipStack = new UiClipRect[MaxClipDepth];
    private readonly float[] _pageWidth = new float[PageCount];
    private readonly float[] _pageHeight = new float[PageCount];
    private readonly UiGlyphTable _glyphs;

    // Gem uniforms, resolved from the sprite table. Offsets: per gem kind three (u, v) deltas from the
    // full stone to the empty glass, the level map and the glints; rects: per kind the full stone's
    // (u, v, du, dv) on its page. Plain arrays so the GL layer uploads them without a copy.
    private readonly float[] _gemOffsets = new float[GemKindCount * 3 * 2];
    private readonly float[] _gemRects = new float[GemKindCount * 4];
    private readonly bool[] _gemReady = new bool[GemKindCount];
    private readonly UiSprite[] _gemFull = new UiSprite[GemKindCount];
    private bool _gemLegacy = true;

    private int _quadCount;
    private int _commandCount;
    private int _clipDepth;
    private int _clipOverflow;
    private UiClipRect _viewport;
    private UiClipRect _clip;

    public UiBatch(UiGlyphTable glyphs)
    {
        _glyphs = glyphs ?? throw new ArgumentNullException(nameof(glyphs));
    }

    // Screen pixels per sprite texel. Border widths of 9-slices and the step of tiles are the art size
    // times this. Set from UiLayout.PixelsPerTexel each frame.
    public float UnitScale { get; set; } = 1f;

    // Multiplier on baked glyph size. 1 draws the font at its baked pixel size (13 and 26 px). Set
    // from UiLayout.TextScale each frame.
    public float TextScale { get; set; } = 1f;

    public int QuadCount => _quadCount;

    public int QuadHeadroom => MaxQuads - _quadCount;

    public int CommandCount => _commandCount;

    // Quads, commands or tile loops that did not fit this frame.
    public int DroppedQuads { get; private set; }

    public ReadOnlySpan<UiVertex> Vertices => new(_vertices, 0, _quadCount * 4);

    public ReadOnlySpan<UiDrawCommand> Commands => new(_commands, 0, _commandCount);

    public int ViewportWidth { get; private set; }

    public int ViewportHeight { get; private set; }

    // The index buffer for any batch: quad q uses vertices 4q..4q+3 (top-left, top-right,
    // bottom-right, bottom-left) as (0,1,2, 0,2,3). Static, so the GL layer uploads it once.
    public static ushort[] CreateQuadIndices()
    {
        ushort[] indices = new ushort[MaxQuads * 6];
        for (int q = 0; q < MaxQuads; q++)
        {
            int v = q * 4;
            int i = q * 6;
            indices[i] = (ushort)v;
            indices[i + 1] = (ushort)(v + 1);
            indices[i + 2] = (ushort)(v + 2);
            indices[i + 3] = (ushort)v;
            indices[i + 4] = (ushort)(v + 2);
            indices[i + 5] = (ushort)(v + 3);
        }

        return indices;
    }

    // Records the image size of a page so sprite texels can be turned into UVs.
    public void SetPageSize(UiPage page, int width, int height)
    {
        _pageWidth[(int)page] = width > 0 ? width : 0f;
        _pageHeight[(int)page] = height > 0 ? height : 0f;
    }

    public void SetSpriteTable(UiSpriteTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (UiPage page in Enum.GetValues<UiPage>())
        {
            if (table.TryGetPageSize(page, out int w, out int h))
            {
                SetPageSize(page, w, h);
            }
        }

        ResolveGems(table);
    }

    // True when the gem of this kind can be drawn in the Gem mode: its full, empty, level map and glint
    // sprites are in the table, on one page of known size, and are the same size. Otherwise Gem draws
    // the plain full sprite and the panel keeps its clipped fallback.
    public bool IsGemReady(UiGemKind kind) => (uint)kind < GemKindCount && _gemReady[(int)kind];

    // True while the mask sprites are the old top-down gradient (the art has no gem setting yet, the
    // marker of the baked level maps). Gem vertices carry it in UiVertexMode.GemLegacyMask.
    public bool GemMasksAreLegacy => _gemLegacy;

    // Six (u, v) pairs for the Gem mode: kind * 3 + 0 empty glass, + 1 level map, + 2 glints, each the
    // UV delta from gem.<k>.full. The fragment stage adds them to the quad's UV.
    public ReadOnlySpan<float> GemOffsets => _gemOffsets;

    // Two (u, v, du, dv) rects, one per kind: the full stone on its page, for gem-local coordinates.
    public ReadOnlySpan<float> GemRects => _gemRects;

    private void ResolveGems(UiSpriteTable table)
    {
        Array.Clear(_gemOffsets);
        Array.Clear(_gemRects);
        Array.Clear(_gemReady);
        Array.Clear(_gemFull);

        // The baked level maps ship with the gem setting; until then the masks are the old gradient.
        _gemLegacy = !(table.Contains(UiSpriteNames.GemSettingLeft) && table.Contains(UiSpriteNames.GemSettingRight));
        ResolveGem(table, UiGemKind.Health);
        ResolveGem(table, UiGemKind.Mana);
    }

    // The four sprites of one gem: the full stone (what the Gem call draws) and the empty glass, level
    // map and glints beside it. True when all four are in the table, on one page of known size, and
    // are the same size. The panel asks this to choose between the liquid gem and its plain fallback,
    // and SetSpriteTable asks it to decide whether the Gem mode can draw, so the two always agree.
    public static bool TryGetGemFamily(UiSpriteTable table, UiGemKind kind, out UiSprite full, out UiSprite empty, out UiSprite mask, out UiSprite spec)
    {
        bool mana = kind == UiGemKind.Mana;
        empty = mask = spec = default;
        if (!table.TryGet(mana ? UiSpriteNames.GemMpFull : UiSpriteNames.GemHpFull, out full)
            || !table.TryGet(mana ? UiSpriteNames.GemMpEmpty : UiSpriteNames.GemHpEmpty, out empty)
            || !table.TryGet(mana ? UiSpriteNames.GemMpMask : UiSpriteNames.GemHpMask, out mask)
            || !table.TryGet(mana ? UiSpriteNames.GemMpSpec : UiSpriteNames.GemHpSpec, out spec)
            || !table.TryGetPageSize(full.Page, out int pw, out int ph)
            || pw <= 0 || ph <= 0
            || empty.Page != full.Page || mask.Page != full.Page || spec.Page != full.Page
            || full.W <= 0 || full.H <= 0
            || empty.W != full.W || empty.H != full.H
            || mask.W != full.W || mask.H != full.H
            || spec.W != full.W || spec.H != full.H)
        {
            return false;
        }

        return true;
    }

    private void ResolveGem(UiSpriteTable table, UiGemKind kind)
    {
        if (!TryGetGemFamily(table, kind, out UiSprite full, out UiSprite empty, out UiSprite mask, out UiSprite spec) || !HasPage(full.Page))
        {
            return;
        }

        int k = (int)kind;
        float pw = _pageWidth[(int)full.Page];
        float ph = _pageHeight[(int)full.Page];
        SetGemOffset(k * 3, empty, full, pw, ph);
        SetGemOffset(k * 3 + 1, mask, full, pw, ph);
        SetGemOffset(k * 3 + 2, spec, full, pw, ph);
        _gemRects[k * 4] = full.X / pw;
        _gemRects[k * 4 + 1] = full.Y / ph;
        _gemRects[k * 4 + 2] = full.W / pw;
        _gemRects[k * 4 + 3] = full.H / ph;
        _gemFull[k] = full;
        _gemReady[k] = true;
    }

    private void SetGemOffset(int slot, in UiSprite other, in UiSprite full, float pageWidth, float pageHeight)
    {
        _gemOffsets[slot * 2] = (other.X - full.X) / pageWidth;
        _gemOffsets[slot * 2 + 1] = (other.Y - full.Y) / pageHeight;
    }

    // One frame canvas for a layout: viewport, texel scale and text scale all come from it.
    public void BeginFrame(in UiLayout layout)
    {
        BeginFrame(layout.ViewportWidth, layout.ViewportHeight, layout.PixelsPerTexel);
        TextScale = layout.TextScale;
    }

    public void BeginFrame(int viewportWidth, int viewportHeight, float unitScale)
    {
        ViewportWidth = Math.Max(viewportWidth, 1);
        ViewportHeight = Math.Max(viewportHeight, 1);
        UnitScale = unitScale;
        _quadCount = 0;
        _commandCount = 0;
        _clipDepth = 0;
        _clipOverflow = 0;
        DroppedQuads = 0;
        _viewport = new UiClipRect(0, 0, ViewportWidth, ViewportHeight);
        _clip = _viewport;
    }

    public static uint Pack(UiColor c) => c.R | ((uint)c.G << 8) | ((uint)c.B << 16) | ((uint)c.A << 24);

    // ---- clip ----

    public void PushClip(UiRect r)
    {
        if (_clipDepth >= MaxClipDepth)
        {
            _clipOverflow++;
            return;
        }

        _clipStack[_clipDepth++] = _clip;
        UiClipRect next = new(
            (int)MathF.Floor(r.X),
            (int)MathF.Floor(r.Y),
            (int)MathF.Ceiling(r.Right),
            (int)MathF.Ceiling(r.Bottom));
        _clip = new UiClipRect(
            Math.Max(_clip.X0, next.X0),
            Math.Max(_clip.Y0, next.Y0),
            Math.Min(_clip.X1, next.X1),
            Math.Min(_clip.Y1, next.Y1));
    }

    public void PopClip()
    {
        if (_clipOverflow > 0)
        {
            _clipOverflow--;
            return;
        }

        if (_clipDepth > 0)
        {
            _clip = _clipStack[--_clipDepth];
        }
    }

    // ---- sprites ----

    public void Sprite(in UiSprite s, UiRect dst, UiColor tint)
    {
        if (!TryUv(in s, 0f, 0f, 1f, 1f, out float u0, out float v0, out float u1, out float v1))
        {
            return;
        }

        AddQuad(s.Page, UiVertexMode.Color, dst.X, dst.Y, dst.Right, dst.Bottom, u0, v0, u1, v1, Pack(tint), 0);
    }

    public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint)
    {
        float sx0 = Math.Clamp(srcNormalized.X, 0f, 1f);
        float sy0 = Math.Clamp(srcNormalized.Y, 0f, 1f);
        float sx1 = Math.Clamp(srcNormalized.Right, 0f, 1f);
        float sy1 = Math.Clamp(srcNormalized.Bottom, 0f, 1f);
        if (sx1 <= sx0 || sy1 <= sy0 || !TryUv(in s, sx0, sy0, sx1, sy1, out float u0, out float v0, out float u1, out float v1))
        {
            return;
        }

        AddQuad(s.Page, UiVertexMode.Color, dst.X, dst.Y, dst.Right, dst.Bottom, u0, v0, u1, v1, Pack(tint), 0);
    }

    public void NineSlice(in UiSprite s, UiRect dst, UiColor tint)
    {
        if (!s.HasSlice)
        {
            Sprite(in s, dst, tint);
            return;
        }

        if (!HasPage(s.Page) || dst.W <= 0f || dst.H <= 0f)
        {
            return;
        }

        // Border sizes in pixels; shrunk together when the destination is smaller than both borders.
        float bl = s.SliceL * UnitScale;
        float br = s.SliceR * UnitScale;
        float bt = s.SliceT * UnitScale;
        float bb = s.SliceB * UnitScale;
        float kx = bl + br > dst.W ? dst.W / (bl + br) : 1f;
        float ky = bt + bb > dst.H ? dst.H / (bt + bb) : 1f;
        bl *= kx;
        br *= kx;
        bt *= ky;
        bb *= ky;

        Span<float> xs = stackalloc float[4] { dst.X, dst.X + bl, dst.Right - br, dst.Right };
        Span<float> ys = stackalloc float[4] { dst.Y, dst.Y + bt, dst.Bottom - bb, dst.Bottom };
        Span<int> tx = stackalloc int[4] { s.X, s.X + s.SliceL, s.X + s.W - s.SliceR, s.X + s.W };
        Span<int> ty = stackalloc int[4] { s.Y, s.Y + s.SliceT, s.Y + s.H - s.SliceB, s.Y + s.H };
        float pw = _pageWidth[(int)s.Page];
        float ph = _pageHeight[(int)s.Page];
        uint packed = Pack(tint);
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                if (xs[col + 1] <= xs[col] || ys[row + 1] <= ys[row] || tx[col + 1] <= tx[col] || ty[row + 1] <= ty[row])
                {
                    continue;
                }

                AddQuad(
                    s.Page,
                    UiVertexMode.Color,
                    xs[col],
                    ys[row],
                    xs[col + 1],
                    ys[row + 1],
                    tx[col] / pw,
                    ty[row] / ph,
                    tx[col + 1] / pw,
                    ty[row + 1] / ph,
                    packed,
                    0);
            }
        }
    }

    public void Tile(in UiSprite s, UiRect dst, UiColor tint)
    {
        if (!HasPage(s.Page) || dst.W <= 0f || dst.H <= 0f)
        {
            return;
        }

        float stepX = s.Tile == UiTile.Y ? dst.W : s.W * UnitScale;
        float stepY = s.Tile == UiTile.X ? dst.H : s.H * UnitScale;
        if (stepX <= 0f || stepY <= 0f)
        {
            return;
        }

        int nx = (int)MathF.Ceiling(dst.W / stepX - 1e-4f);
        int ny = (int)MathF.Ceiling(dst.H / stepY - 1e-4f);
        if (nx < 1 || ny < 1)
        {
            return;
        }

        if ((long)nx * ny > MaxQuads - _quadCount)
        {
            DroppedQuads++;
            return;
        }

        float pw = _pageWidth[(int)s.Page];
        float ph = _pageHeight[(int)s.Page];
        float baseU0 = s.X / pw;
        float baseV0 = s.Y / ph;
        float baseU1 = (s.X + s.W) / pw;
        float baseV1 = (s.Y + s.H) / ph;
        uint packed = Pack(tint);
        for (int iy = 0; iy < ny; iy++)
        {
            float y0 = dst.Y + iy * stepY;
            float y1 = MathF.Min(y0 + stepY, dst.Bottom);
            float fy = (y1 - y0) / stepY;
            for (int ix = 0; ix < nx; ix++)
            {
                float x0 = dst.X + ix * stepX;
                float x1 = MathF.Min(x0 + stepX, dst.Right);
                float fx = (x1 - x0) / stepX;
                AddQuad(
                    s.Page,
                    UiVertexMode.Color,
                    x0,
                    y0,
                    x1,
                    y1,
                    baseU0,
                    baseV0,
                    baseU0 + (baseU1 - baseU0) * fx,
                    baseV0 + (baseV1 - baseV0) * fy,
                    packed,
                    0);
            }
        }
    }

    public void Solid(UiRect dst, UiColor color)
    {
        AddQuad(UiPage.Hud, UiVertexMode.Solid, dst.X, dst.Y, dst.Right, dst.Bottom, 0f, 0f, 0f, 0f, Pack(color), 0);
    }

    public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim)
    {
        if (!TryUv(in s, 0f, 0f, 1f, 1f, out float u0, out float v0, out float u1, out float v1))
        {
            return;
        }

        AddQuad(s.Page, UiVertexMode.Mask, dst.X, dst.Y, dst.Right, dst.Bottom, u0, v0, u1, v1, Pack(fill), Pack(rim));
    }

    // A liquid gem as one quad of the Hud page: the Gem vertex mode reads the empty glass, the level map
    // and the glints at fixed UV offsets from the full stone. The four 0..1 values ride in the rim bytes
    // (8 bits each: a 112 px gem moves less than half a pixel per step). When the gem's sprites are not
    // all in the table, or the sprite is not the table's full stone, the plain sprite is drawn.
    public void Gem(in UiSprite full, UiRect dst, UiGemKind kind, float fill, float phase, float pulse, float sweep, UiColor tint)
    {
        int k = (int)kind;
        if (!IsGemReady(kind) || full != _gemFull[k])
        {
            Sprite(in full, dst, tint);
            return;
        }

        if (!TryUv(in full, 0f, 0f, 1f, 1f, out float u0, out float v0, out float u1, out float v1))
        {
            return;
        }

        uint rim = QuantizeUnit(fill) | (QuantizeUnit(phase) << 8) | (QuantizeUnit(pulse) << 16) | (QuantizeUnit(sweep) << 24);
        uint mode = UiVertexMode.Gem | ((uint)k << UiVertexMode.GemKindShift) | (_gemLegacy ? UiVertexMode.GemLegacyMask : 0u);
        AddQuad(full.Page, mode, dst.X, dst.Y, dst.Right, dst.Bottom, u0, v0, u1, v1, Pack(tint), rim);
    }

    public void Additive(in UiSprite s, UiRect dst, UiColor tint)
    {
        if (!TryUv(in s, 0f, 0f, 1f, 1f, out float u0, out float v0, out float u1, out float v1))
        {
            return;
        }

        AddQuad(s.Page, UiVertexMode.Additive, dst.X, dst.Y, dst.Right, dst.Bottom, u0, v0, u1, v1, Pack(tint), 0);
    }

    // 0..1 to a byte, NaN as 0.
    private static uint QuantizeUnit(float v) => float.IsNaN(v) ? 0u : (uint)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);

    // ---- text ----

    // Draws one line, pos being the top-left of the line box. Control characters, characters the
    // font lacks and anything outside Latin-1 draw '?' (a surrogate pair draws one '?'). Returns the
    // advance in pixels.
    public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color)
    {
        uint packed = Pack(color);
        float ts = TextScale;
        float pen = pos.X;
        float top = pos.Y;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                c = UiGlyphTable.Fallback;
            }

            if (!_glyphs.TryGet(style, c, out UiGlyph g))
            {
                continue;
            }

            if (g.X1 > g.X0 && g.Y1 > g.Y0)
            {
                AddQuad(
                    UiPage.Font,
                    UiVertexMode.Color,
                    MathF.Round(pen + g.X0 * ts),
                    MathF.Round(top + g.Y0 * ts),
                    MathF.Round(pen + g.X1 * ts),
                    MathF.Round(top + g.Y1 * ts),
                    g.U0,
                    g.V0,
                    g.U1,
                    g.V1,
                    packed,
                    0);
            }

            pen += g.Advance * ts;
        }

        return pen - pos.X;
    }

    public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style)
    {
        float width = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                c = UiGlyphTable.Fallback;
            }

            if (_glyphs.TryGet(style, c, out UiGlyph g))
            {
                width += g.Advance * TextScale;
            }
        }

        return width;
    }

    // Line box height in pixels for a style at the current TextScale.
    public float LineHeight(UiTextStyle style) => _glyphs.LineHeight(style) * TextScale;

    // ---- internals ----

    private bool HasPage(UiPage page) => _pageWidth[(int)page] > 0f && _pageHeight[(int)page] > 0f;

    // UVs of a sub-rect (0..1 of the sprite) of a sprite. False when the page size is unknown.
    private bool TryUv(in UiSprite s, float fx0, float fy0, float fx1, float fy1, out float u0, out float v0, out float u1, out float v1)
    {
        u0 = v0 = u1 = v1 = 0f;
        if (!HasPage(s.Page))
        {
            return false;
        }

        float pw = _pageWidth[(int)s.Page];
        float ph = _pageHeight[(int)s.Page];
        u0 = (s.X + s.W * fx0) / pw;
        u1 = (s.X + s.W * fx1) / pw;
        v0 = (s.Y + s.H * fy0) / ph;
        v1 = (s.Y + s.H * fy1) / ph;
        return true;
    }

    private void AddQuad(UiPage page, uint mode, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint tint, uint rim)
    {
        if (x1 <= x0 || y1 <= y0 || float.IsNaN(x0 + y0 + x1 + y1))
        {
            return;
        }

        UiClipRect clip = _clip;
        if (clip.IsEmpty || x1 <= clip.X0 || x0 >= clip.X1 || y1 <= clip.Y0 || y0 >= clip.Y1)
        {
            return;
        }

        if (_quadCount >= MaxQuads)
        {
            DroppedQuads++;
            return;
        }

        bool solid = mode == UiVertexMode.Solid;
        int cmdIndex = _commandCount - 1;
        bool merge = false;
        if (cmdIndex >= 0)
        {
            ref UiDrawCommand last = ref _commands[cmdIndex];
            if (last.Clip == clip)
            {
                if (solid || last.Page == page)
                {
                    merge = true;
                }
                else if (last.PageFree)
                {
                    last.Page = page;
                    last.PageFree = false;
                    merge = true;
                }
            }
        }

        if (merge)
        {
            _commands[cmdIndex].IndexCount += 6;
        }
        else
        {
            if (_commandCount >= MaxCommands)
            {
                DroppedQuads++;
                return;
            }

            _commands[_commandCount++] = new UiDrawCommand
            {
                Page = page,
                Clip = clip,
                FirstIndex = _quadCount * 6,
                IndexCount = 6,
                PageFree = solid,
            };
        }

        int v = _quadCount * 4;
        _quadCount++;
        _vertices[v] = new UiVertex { X = x0, Y = y0, U = u0, V = v0, Tint = tint, Rim = rim, Mode = mode };
        _vertices[v + 1] = new UiVertex { X = x1, Y = y0, U = u1, V = v0, Tint = tint, Rim = rim, Mode = mode };
        _vertices[v + 2] = new UiVertex { X = x1, Y = y1, U = u1, V = v1, Tint = tint, Rim = rim, Mode = mode };
        _vertices[v + 3] = new UiVertex { X = x0, Y = y1, U = u0, V = v1, Tint = tint, Rim = rim, Mode = mode };
    }
}
