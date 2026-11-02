using System.Text.Json;

namespace Dream.Rendering.Ui;

// A sprite JSON or its PNG is unusable. The classic HUD treats this as a latched failure.
public sealed class UiSpriteTableException(string message) : Exception(message);

// Name -> sprite for every atlas page, parsed from the JSON files in ui/. GL-free. Sprite x, y, w,
// h in the JSON exclude the extrusion border, so they are used as is. Build it with TryLoad (reads
// the real files) or AddPage (fixtures), then Validate.
public sealed class UiSpriteTable
{
    // Page JSON files relative to the ui directory. Uncommon and Rare reuse the ItemsCommon
    // rects, so they have a PNG only.
    private static readonly (UiPage Page, string Json, string Png)[] PageFiles =
    [
        (UiPage.Window, "window/window.json", "window/window.png"),
        (UiPage.Hud, "hud/hud.json", "hud/hud.png"),
        (UiPage.Cursors, "hud/cursors.json", "hud/cursors.png"),
        (UiPage.ItemsCommon, "items/items.json", "items/items.png"),
        (UiPage.Digits, "fx/dmg_digits_x4.json", "fx/dmg_digits_x4.png"),
    ];

    // The front-end page. TryLoad (the HUD table) does not read it; TryLoadPages does.
    private const string MenuJson = "menu/menu.json";

    private readonly Dictionary<string, UiSprite> _sprites = new(StringComparer.Ordinal);
    private readonly Dictionary<UiPage, (int Width, int Height)> _pageSizes = [];

    public int Count => _sprites.Count;

    public IEnumerable<string> Names => _sprites.Keys;

    public bool Contains(string name) => _sprites.ContainsKey(name);

    public bool TryGet(string name, out UiSprite sprite) => _sprites.TryGetValue(name, out sprite);

    // Throws KeyNotFoundException for an unknown name; use it for names Validate checked.
    public UiSprite Get(string name) => _sprites.TryGetValue(name, out UiSprite s)
        ? s
        : throw new KeyNotFoundException($"UI sprite '{name}' is not in the table.");

    public bool TryGetPageSize(UiPage page, out int width, out int height)
    {
        bool found = _pageSizes.TryGetValue(page, out (int Width, int Height) size);
        width = size.Width;
        height = size.Height;
        return found;
    }

    // Path of the PNG for a page relative to the ui directory, or null for Font.
    public static string? PagePngPath(UiPage page) => page switch
    {
        UiPage.Window => "window/window.png",
        UiPage.Hud => "hud/hud.png",
        UiPage.Cursors => "hud/cursors.png",
        UiPage.ItemsCommon => "items/items.png",
        UiPage.ItemsUncommon => "items/items_uncommon.png",
        UiPage.ItemsRare => "items/items_rare.png",
        UiPage.Digits => "fx/dmg_digits_x4.png",
        UiPage.Menu => "menu/menu.png",
        _ => null
    };

    // Reads every page from uiDirectory and validates. Never throws: any failure (missing file,
    // corrupt PNG, bad JSON, size mismatch, out-of-bounds sprite, missing required sprite) returns
    // false with a one-line reason.
    public static bool TryLoad(string uiDirectory, out UiSpriteTable? table, out string error)
    {
        table = null;
        error = string.Empty;
        try
        {
            UiSpriteTable result = new();
            foreach ((UiPage page, string jsonRel, string pngRel) in PageFiles)
            {
                string json = File.ReadAllText(Path.Combine(uiDirectory, jsonRel));
                (int w, int h) = ReadPngSize(Path.Combine(uiDirectory, pngRel));
                result.AddPage(page, json, w, h);
            }

            // The tier pages are drawn with the common rects, so they must be the same size.
            UiSpriteTable.TryGetSize(result, UiPage.ItemsCommon, out int cw, out int ch);
            foreach (UiPage tier in new[] { UiPage.ItemsUncommon, UiPage.ItemsRare })
            {
                string rel = PagePngPath(tier)!;
                (int w, int h) = ReadPngSize(Path.Combine(uiDirectory, rel));
                if (w != cw || h != ch)
                {
                    throw new UiSpriteTableException($"{rel} is {w}x{h} but items.png is {cw}x{ch}.");
                }

                result._pageSizes[tier] = (w, h);
            }

            result.Validate();
            table = result;
            return true;
        }
        catch (Exception ex) when (ex is UiSpriteTableException or IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            error = ex.Message;
            return false;
        }
    }

    // Reads only the listed pages (the front end needs Window and Menu, not the whole HUD) and does not
    // check UiSpriteNames.Required. Never throws: any failure returns false with a one-line reason.
    public static bool TryLoadPages(string uiDirectory, IReadOnlyList<UiPage> pages, out UiSpriteTable? table, out string error)
    {
        table = null;
        error = string.Empty;
        try
        {
            UiSpriteTable result = new();
            foreach (UiPage page in pages)
            {
                string png = PagePngPath(page) ?? throw new UiSpriteTableException($"{page} has no image file.");
                string jsonRel = page == UiPage.Menu
                    ? MenuJson
                    : PageFiles.First(f => f.Page == page).Json;
                string json = File.ReadAllText(Path.Combine(uiDirectory, jsonRel));
                (int w, int h) = ReadPngSize(Path.Combine(uiDirectory, png));
                result.AddPage(page, json, w, h);
            }

            table = result;
            return true;
        }
        catch (Exception ex) when (ex is UiSpriteTableException or IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            error = ex.Message;
            return false;
        }
    }

    // Parses one page. pngWidth/pngHeight are the real image size; the JSON "size" must match it
    // and every sprite must lie inside it. Throws UiSpriteTableException (or JsonException).
    public void AddPage(UiPage page, string json, int pngWidth, int pngHeight)
    {
        if (page == UiPage.Font)
        {
            throw new UiSpriteTableException("The font page has no sprite JSON.");
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new UiSpriteTableException($"{page}: JSON root is not an object.");
        }

        JsonElement size = Require(root, "size", page);
        if (size.ValueKind != JsonValueKind.Array || size.GetArrayLength() != 2
            || size[0].GetInt32() != pngWidth || size[1].GetInt32() != pngHeight)
        {
            throw new UiSpriteTableException($"{page}: JSON size does not match the {pngWidth}x{pngHeight} image.");
        }

        JsonElement sprites = Require(root, "sprites", page);
        if (sprites.ValueKind != JsonValueKind.Object)
        {
            throw new UiSpriteTableException($"{page}: 'sprites' is not an object.");
        }

        Dictionary<string, UiSprite> parsed = new(StringComparer.Ordinal);
        foreach (JsonProperty p in sprites.EnumerateObject())
        {
            if (_sprites.ContainsKey(p.Name))
            {
                throw new UiSpriteTableException($"{page}: sprite '{p.Name}' is already defined on another page.");
            }

            parsed[p.Name] = ParseSprite(page, p.Name, p.Value, pngWidth, pngHeight);
        }

        foreach (KeyValuePair<string, UiSprite> kv in parsed)
        {
            _sprites[kv.Key] = kv.Value;
        }

        _pageSizes[page] = (pngWidth, pngHeight);
    }

    // Throws UiSpriteTableException if a name in UiSpriteNames.Required is missing.
    public void Validate()
    {
        foreach (string name in UiSpriteNames.Required)
        {
            if (!_sprites.ContainsKey(name))
            {
                throw new UiSpriteTableException($"Required UI sprite '{name}' is missing.");
            }
        }
    }

    private static void TryGetSize(UiSpriteTable t, UiPage page, out int w, out int h)
    {
        if (!t.TryGetPageSize(page, out w, out h))
        {
            throw new UiSpriteTableException($"{page} was not loaded.");
        }
    }

    private static UiSprite ParseSprite(UiPage page, string name, JsonElement e, int pageW, int pageH)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new UiSpriteTableException($"{page}: sprite '{name}' is not an object.");
        }

        int x = Require(e, "x", page).GetInt32();
        int y = Require(e, "y", page).GetInt32();
        int w = Require(e, "w", page).GetInt32();
        int h = Require(e, "h", page).GetInt32();
        if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > pageW || y + h > pageH)
        {
            throw new UiSpriteTableException($"{page}: sprite '{name}' ({x},{y} {w}x{h}) is outside the {pageW}x{pageH} image.");
        }

        int pivotX = 0;
        int pivotY = 0;
        if (e.TryGetProperty("pivot", out JsonElement pivot) && pivot.ValueKind == JsonValueKind.Array && pivot.GetArrayLength() == 2)
        {
            pivotX = pivot[0].GetInt32();
            pivotY = pivot[1].GetInt32();
        }

        int sl = 0, st = 0, sr = 0, sb = 0;
        if (e.TryGetProperty("slice", out JsonElement slice) && slice.ValueKind == JsonValueKind.Array)
        {
            if (slice.GetArrayLength() != 4)
            {
                throw new UiSpriteTableException($"{page}: sprite '{name}' slice must have 4 values.");
            }

            sl = slice[0].GetInt32();
            st = slice[1].GetInt32();
            sr = slice[2].GetInt32();
            sb = slice[3].GetInt32();
            if (sl < 0 || st < 0 || sr < 0 || sb < 0 || sl + sr > w || st + sb > h)
            {
                throw new UiSpriteTableException($"{page}: sprite '{name}' slice does not fit its {w}x{h} size.");
            }
        }

        UiTile tile = UiTile.None;
        if (e.TryGetProperty("tile", out JsonElement tileEl) && tileEl.ValueKind == JsonValueKind.String)
        {
            tile = tileEl.GetString() switch
            {
                "none" => UiTile.None,
                "x" => UiTile.X,
                "y" => UiTile.Y,
                "xy" => UiTile.XY,
                var other => throw new UiSpriteTableException($"{page}: sprite '{name}' has unknown tile '{other}'.")
            };
        }

        int cols = 1;
        int rows = 1;
        if (e.TryGetProperty("meta", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("footprint", out JsonElement fp) && fp.ValueKind == JsonValueKind.Array && fp.GetArrayLength() == 2)
        {
            cols = fp[0].GetInt32();
            rows = fp[1].GetInt32();
            if (cols < 1 || rows < 1)
            {
                throw new UiSpriteTableException($"{page}: sprite '{name}' has a footprint below 1x1.");
            }
        }

        return new UiSprite(page, x, y, w, h, pivotX, pivotY, sl, st, sr, sb, tile, cols, rows);
    }

    private static JsonElement Require(JsonElement e, string key, UiPage page) =>
        e.TryGetProperty(key, out JsonElement v) ? v : throw new UiSpriteTableException($"{page}: missing '{key}'.");

    // Width and height from the PNG header (IHDR). Throws UiSpriteTableException when it is not a PNG.
    public static (int Width, int Height) ReadPngSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using FileStream fs = File.OpenRead(path);
        int read = 0;
        while (read < header.Length)
        {
            int n = fs.Read(header[read..]);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (read < 24 || !header[..8].SequenceEqual(signature) || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new UiSpriteTableException($"{Path.GetFileName(path)} is not a PNG.");
        }

        int w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        int h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
        if (w <= 0 || h <= 0)
        {
            throw new UiSpriteTableException($"{Path.GetFileName(path)} has an invalid size.");
        }

        return (w, h);
    }
}
