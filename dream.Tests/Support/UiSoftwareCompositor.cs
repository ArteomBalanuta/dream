using Dream.Assets;
using Dream.Rendering.Ui;
using ImGuiNET;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dream.Tests.Support;

// RGBA8 straight-alpha pixels with a top-left origin.
public sealed class UiTexturePixels(int width, int height, byte[] rgba)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Rgba { get; } = rgba;

    public static UiTexturePixels Load(string path)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(path);
        byte[] data = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(data);
        return new UiTexturePixels(image.Width, image.Height, data);
    }

    public static UiTexturePixels Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        byte[] data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
            data[i + 3] = a;
        }

        return new UiTexturePixels(width, height, data);
    }
}

// Headless stand-in for UiGlRenderer: rasterizes a UiBatch into an image on the CPU with the same
// draw commands, the same vertex modes and the same premultiplied blend (ONE, ONE_MINUS_SRC_ALPHA).
// Sampling is bilinear with clamp-to-edge and no mip levels. It proves layout, UVs, clipping and
// z-order, not GL state. Pixel centres decide coverage, like the GL fill rule for these quads.
// The Gem and Additive modes follow uiFragment.glsl line by line (same constants, same order), with
// the gem offsets and rects the GL layer uploads read from the batch, so a preview of the liquid gem
// is the shader's own arithmetic. The framebuffer is 8-bit, so each blend result clamps to 0..1.
public sealed class UiSoftwareCompositor
{
    private readonly float[] _canvas; // premultiplied RGBA, 0..1
    private readonly Dictionary<UiPage, UiTexturePixels> _pages = [];
    private UiBatch? _batch;

    public UiSoftwareCompositor(int width, int height)
    {
        Width = width;
        Height = height;
        _canvas = new float[width * height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    public void SetPage(UiPage page, UiTexturePixels pixels) => _pages[page] = pixels;

    // Registers every PNG page of the real atlas from the ui directory.
    public void LoadRealPages(string uiDirectory)
    {
        foreach (UiPage page in UiAtlasStore.FilePages)
        {
            SetPage(page, UiTexturePixels.Load(Path.Combine(uiDirectory, UiSpriteTable.PagePngPath(page)!)));
        }
    }

    // A flat dark blue-grey with a faint checker, so transparent areas and alpha edges are visible.
    public void ClearBackdrop()
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                bool alt = ((x >> 4) + (y >> 4) & 1) == 0;
                float v = alt ? 0.16f : 0.2f;
                int i = (y * Width + x) * 4;
                _canvas[i] = v * 0.8f;
                _canvas[i + 1] = v * 0.9f;
                _canvas[i + 2] = v;
                _canvas[i + 3] = 1f;
            }
        }
    }

    public void Draw(UiBatch batch)
    {
        _batch = batch;
        ReadOnlySpan<UiVertex> vertices = batch.Vertices;
        foreach (UiDrawCommand cmd in batch.Commands)
        {
            _pages.TryGetValue(cmd.Page, out UiTexturePixels? texture);
            int firstQuad = cmd.FirstIndex / 6;
            int quadCount = cmd.IndexCount / 6;
            for (int q = firstQuad; q < firstQuad + quadCount; q++)
            {
                DrawQuad(vertices.Slice(q * 4, 4), texture, cmd.Clip);
            }
        }
    }

    public Rgba32 GetRgba(int x, int y) => ToRgba32((y * Width + x) * 4);

    public void SavePng(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using Image<Rgba32> image = new(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                image[x, y] = ToRgba32((y * Width + x) * 4);
            }
        }

        image.SaveAsPng(path);
    }

    private Rgba32 ToRgba32(int i) => new(
        ToByte(_canvas[i]),
        ToByte(_canvas[i + 1]),
        ToByte(_canvas[i + 2]),
        255);

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    private void DrawQuad(ReadOnlySpan<UiVertex> v, UiTexturePixels? texture, UiClipRect clip)
    {
        UiVertex tl = v[0];
        UiVertex br = v[2];
        uint word = tl.Mode;
        uint mode = UiVertexMode.Base(word);
        if (mode != UiVertexMode.Solid && texture == null)
        {
            return;
        }

        int x0 = Math.Max(clip.X0, (int)MathF.Ceiling(tl.X - 0.5f));
        int x1 = Math.Min(Math.Min(clip.X1, Width), (int)MathF.Ceiling(br.X - 0.5f));
        int y0 = Math.Max(clip.Y0, (int)MathF.Ceiling(tl.Y - 0.5f));
        int y1 = Math.Min(Math.Min(clip.Y1, Height), (int)MathF.Ceiling(br.Y - 0.5f));
        x0 = Math.Max(x0, 0);
        y0 = Math.Max(y0, 0);
        float w = br.X - tl.X;
        float h = br.Y - tl.Y;
        if (w <= 0f || h <= 0f)
        {
            return;
        }

        float tr = (tl.Tint & 0xFF) / 255f;
        float tg = ((tl.Tint >> 8) & 0xFF) / 255f;
        float tb = ((tl.Tint >> 16) & 0xFF) / 255f;
        float ta = ((tl.Tint >> 24) & 0xFF) / 255f;
        float rr = (tl.Rim & 0xFF) / 255f;
        float rg = ((tl.Rim >> 8) & 0xFF) / 255f;
        float rb = ((tl.Rim >> 16) & 0xFF) / 255f;
        float ra = ((tl.Rim >> 24) & 0xFF) / 255f;

        for (int y = y0; y < y1; y++)
        {
            float fy = (y + 0.5f - tl.Y) / h;
            for (int x = x0; x < x1; x++)
            {
                float fx = (x + 0.5f - tl.X) / w;
                float sr;
                float sg;
                float sb;
                float sa;
                if (mode == UiVertexMode.Solid)
                {
                    sr = tr * ta;
                    sg = tg * ta;
                    sb = tb * ta;
                    sa = ta;
                }
                else
                {
                    float u = tl.U + (br.U - tl.U) * fx;
                    float vv = tl.V + (br.V - tl.V) * fy;
                    Sample(texture!, u, vv, out float cr, out float cg, out float cb, out float ca);
                    if (mode == UiVertexMode.Gem)
                    {
                        ShadeGem(texture!, word, u, vv, (tr, tg, tb, ta), (rr, rg, rb, ra), (cr, cg, cb, ca), out sr, out sg, out sb, out sa);
                    }
                    else if (mode == UiVertexMode.Additive)
                    {
                        // Output (rgb * a, 0): the blend then adds it to what is there.
                        float add = ca * ta;
                        sr = cr * tr * add;
                        sg = cg * tg * add;
                        sb = cb * tb * add;
                        sa = 0f;
                    }
                    else if (mode == UiVertexMode.Mask)
                    {
                        float fill = cr * ta;
                        float rim = cg * ra;
                        sr = fill * tr + rim * rr;
                        sg = fill * tg + rim * rg;
                        sb = fill * tb + rim * rb;
                        sa = fill + rim;
                    }
                    else
                    {
                        sa = ca * ta;
                        sr = cr * tr * sa;
                        sg = cg * tg * sa;
                        sb = cb * tb * sa;
                    }
                }

                int i = (y * Width + x) * 4;
                float inv = 1f - sa;
                _canvas[i] = MathF.Min(sr + _canvas[i] * inv, 1f);
                _canvas[i + 1] = MathF.Min(sg + _canvas[i + 1] * inv, 1f);
                _canvas[i + 2] = MathF.Min(sb + _canvas[i + 2] * inv, 1f);
                _canvas[i + 3] = MathF.Min(sa + _canvas[i + 3] * inv, 1f);
            }
        }
    }

    // The Gem branch of uiFragment.glsl. (u, v) is the quad's UV at this pixel and c the sample of the full
    // stone there; the empty glass, level map and glints are read at the offsets the batch holds.
    private void ShadeGem(
        UiTexturePixels texture,
        uint word,
        float u,
        float v,
        (float R, float G, float B, float A) tint,
        (float R, float G, float B, float A) rim,
        (float R, float G, float B, float A) c,
        out float sr,
        out float sg,
        out float sb,
        out float sa)
    {
        UiBatch batch = _batch!;
        int k = (int)((word >> UiVertexMode.GemKindShift) & 1u);
        bool legacy = (word & UiVertexMode.GemLegacyMask) != 0;
        ReadOnlySpan<float> offsets = batch.GemOffsets;
        ReadOnlySpan<float> rects = batch.GemRects;
        float localX = (u - rects[k * 4]) / rects[k * 4 + 2];
        float localY = (v - rects[k * 4 + 1]) / rects[k * 4 + 3];
        Sample(texture, u + offsets[3 * k * 2], v + offsets[3 * k * 2 + 1], out float gr, out float gg, out float gb, out float ga);
        Sample(texture, u + offsets[(3 * k + 1) * 2], v + offsets[(3 * k + 1) * 2 + 1], out float dr, out float dg, out float db, out float _);
        Sample(texture, u + offsets[(3 * k + 2) * 2], v + offsets[(3 * k + 2) * 2 + 1], out float lr, out float lg, out float lb, out float la);

        const float tau = 6.2831853f;
        float thr = legacy ? 1f - dr : dr;
        float wobble = legacy ? 0f : 0.006f * MathF.Sin(tau * (rim.G + db));
        float lvl = Mix(-0.03f, 1.03f, rim.R) + wobble;
        float liquid = SmoothStep(thr - 0.012f, thr + 0.012f, lvl);
        float r = Mix(gr, c.R, liquid);
        float g = Mix(gg, c.G, liquid);
        float b = Mix(gb, c.B, liquid);

        float ends = SmoothStep(0f, 0.03f, rim.R) * (1f - SmoothStep(0.97f, 1f, rim.R));
        float e = (thr - lvl) / 0.018f;
        float meniscus = MathF.Exp(-e * e) * (legacy ? 1f : dg) * ends * 1.2f;
        r += meniscus * c.R;
        g += meniscus * c.G;
        b += meniscus * c.B;

        float s = (0.5f * (localX + localY) - Mix(-0.25f, 1.25f, rim.A)) / 0.09f;
        float glint = la * MathF.Exp(-s * s);
        r += lr * glint;
        g += lg * glint;
        b += lb * glint;

        float pulse = 1f + 0.35f * rim.B * liquid;
        r = MathF.Min(r * pulse * tint.R, 1f);
        g = MathF.Min(g * pulse * tint.G, 1f);
        b = MathF.Min(b * pulse * tint.B, 1f);
        sa = Mix(ga, c.A, liquid) * tint.A;
        sr = r * sa;
        sg = g * sa;
        sb = b * sa;
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Bilinear sample, clamp to edge, straight alpha (like the GL texture).
    private static void Sample(UiTexturePixels t, float u, float v, out float r, out float g, out float b, out float a)
    {
        float px = u * t.Width - 0.5f;
        float py = v * t.Height - 0.5f;
        int ix = (int)MathF.Floor(px);
        int iy = (int)MathF.Floor(py);
        float fx = px - ix;
        float fy = py - iy;
        r = g = b = a = 0f;
        for (int dy = 0; dy < 2; dy++)
        {
            int sy = Math.Clamp(iy + dy, 0, t.Height - 1);
            float wy = dy == 0 ? 1f - fy : fy;
            for (int dx = 0; dx < 2; dx++)
            {
                int sx = Math.Clamp(ix + dx, 0, t.Width - 1);
                float wgt = (dx == 0 ? 1f - fx : fx) * wy;
                int i = (sy * t.Width + sx) * 4;
                r += t.Rgba[i] * wgt;
                g += t.Rgba[i + 1] * wgt;
                b += t.Rgba[i + 2] * wgt;
                a += t.Rgba[i + 3] * wgt;
            }
        }

        r /= 255f;
        g /= 255f;
        b /= 255f;
        a /= 255f;
    }
}

// Entry points for producing review PNGs. Output goes to artifacts/hud-preview/ in the repo (the
// folder is git-ignored) when DREAM_HUD_PREVIEW=1; otherwise nothing is written.
public static class HudPreview
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DREAM_HUD_PREVIEW") == "1";

    public static string OutputDirectory
    {
        get
        {
            return Path.Combine(Dream.Tests.Integration.RepoRoot.Find(), "artifacts", "hud-preview");
        }
    }

    // The three review sizes from the plan.
    public static readonly (int Width, int Height)[] Resolutions = [(1280, 720), (1920, 1080), (1440, 1024)];

    // Renders a scene with the real atlas and font-less glyph table (each glyph a solid block) into
    // a PNG named name_WxH.png. draw receives the batch, the sprite table and the layout. Returns
    // the compositor so tests can probe pixels, and writes the file when Enabled or forceWrite.
    public static UiSoftwareCompositor Render(
        string name,
        int width,
        int height,
        Action<UiBatch, UiSpriteTable, UiLayout> draw,
        float configuredScale = 0f,
        UiGlyphTable? glyphs = null,
        UiTexturePixels? fontPage = null,
        bool forceWrite = false)
    {
        string uiDirectory = AssetPaths.UiDirectory;
        if (!UiSpriteTable.TryLoad(uiDirectory, out UiSpriteTable? table, out string error))
        {
            throw new InvalidOperationException(error);
        }

        UiLayout layout = UiLayout.Create(width, height, configuredScale);
        UiBatch batch = new(glyphs ?? BlockGlyphs.Create());
        batch.SetSpriteTable(table!);
        batch.BeginFrame(in layout);
        draw(batch, table!, layout);

        UiSoftwareCompositor compositor = new(width, height);
        compositor.LoadRealPages(uiDirectory);
        compositor.SetPage(UiPage.Font, fontPage ?? BlockGlyphs.Page);
        compositor.ClearBackdrop();
        compositor.Draw(batch);
        if (Enabled || forceWrite)
        {
            compositor.SavePng(Path.Combine(OutputDirectory, $"{name}_{width}x{height}.png"));
        }

        return compositor;
    }
}

// A glyph table where every printable character is a solid block, for previews that must not need
// the ImGui native library. Real text appears when UiFontSetup supplies the table.
public static class BlockGlyphs
{
    public static UiTexturePixels Page { get; } = UiTexturePixels.Solid(8, 8, 255, 255, 255, 255);

    public static UiGlyphTable Create()
    {
        UiGlyphTable table = new(13f, 26f);
        for (char c = (char)UiGlyphTable.FirstCodepoint; c <= UiGlyphTable.LastCodepoint; c++)
        {
            table.Set(UiTextStyle.Body, c, new UiGlyph(0f, 2f, c == ' ' ? 0f : 6f, 11f, 0f, 0f, 1f, 1f, 7f));
            table.Set(UiTextStyle.Large, c, new UiGlyph(0f, 4f, c == ' ' ? 0f : 12f, 22f, 0f, 0f, 1f, 1f, 14f));
        }

        return table;
    }
}

// An IUiInput that never reports a click or hover, for previews and panel tests that only draw.
public sealed class NullUiInput : IUiInput
{
    public bool Button(int id, UiRect r) => false;

    public bool Hovered(int id, UiRect r) => false;

    public bool RightClicked(int id, UiRect r) => false;

    public bool BeginDrag(int id, UiRect r, int payload) => false;

    public bool TryEndDrag(out int payload, out OpenTK.Mathematics.Vector2 dropPosition)
    {
        payload = -1;
        dropPosition = default;
        return false;
    }

    public int ActiveDragPayload => -1;

    public bool ModalActive { get; set; }

    public UiPointer Pointer => default;
}

// The one lock for test code that makes an ImGui context current: the current context is process-wide
// and xUnit runs test classes in parallel.
public static class ImGuiTestGate
{
    public static readonly object Gate = new();
}

// The real baked ImGui font (body 13 px and large 26 px) as a glyph table plus its texture, built in
// a throwaway ImGui context. Needs the ImGui native library but no GL context or window.
public static class RealFont
{
    private static (UiGlyphTable, UiTexturePixels)? s_cached;

    public static (UiGlyphTable Glyphs, UiTexturePixels Page) Get()
    {
        lock (ImGuiTestGate.Gate)
        {
            if (s_cached is { } cached)
            {
                return cached;
            }

            IntPtr context = ImGui.CreateContext();
            try
            {
                ImGui.SetCurrentContext(context);
                ImGuiIOPtr io = ImGui.GetIO();
                UiFontSetup.AddFonts(io);
                io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
                byte[] rgba = new byte[width * height * 4];
                System.Runtime.InteropServices.Marshal.Copy(pixels, rgba, 0, rgba.Length);
                UiGlyphTable table = UiFontSetup.BuildGlyphTable(io.Fonts);
                s_cached = (table, new UiTexturePixels(width, height, rgba));
                return s_cached.Value;
            }
            finally
            {
                ImGui.DestroyContext(context);
            }
        }
    }
}
