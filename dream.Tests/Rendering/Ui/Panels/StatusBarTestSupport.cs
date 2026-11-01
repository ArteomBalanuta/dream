using Dream.Assets;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Players;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

public enum StatusBarCallKind
{
    Sprite,
    Clipped,
    NineSlice,
    Tile,
    Solid,
    Mask,
    Text
}

public readonly record struct StatusBarCall(
    StatusBarCallKind Kind,
    UiRect Rect,
    UiRect Src,
    UiColor Color,
    string? Name,
    string? Text,
    Vector2 TextPos);

// Fake IUiCanvas for the status bar, notice, label and cursor panels. With Recording off it only
// counts, so allocation tests measure the panel and not the recorder. Sprites are named by
// reverse lookup in the real table.
public sealed class StatusBarRecordingCanvas : IUiCanvas
{
    private readonly Dictionary<UiSprite, string>? _names;

    public StatusBarRecordingCanvas(UiSpriteTable table, bool recording = true)
    {
        Recording = recording;
        _names = new Dictionary<UiSprite, string>();
        foreach (string name in table.Names)
        {
            // Uncommon and Rare reuse rects but not pages, so the record key includes the page.
            _names[table.Get(name)] = name;
        }
    }

    public bool Recording { get; }

    public List<StatusBarCall> Calls { get; } = [];

    public int Count { get; private set; }

    public IEnumerable<StatusBarCall> Sprites(string name) =>
        Calls.Where(c => c.Kind is StatusBarCallKind.Sprite or StatusBarCallKind.Clipped && c.Name == name);

    public IEnumerable<StatusBarCall> Texts => Calls.Where(c => c.Kind == StatusBarCallKind.Text);

    public void Sprite(in UiSprite s, UiRect dst, UiColor tint) => Add(StatusBarCallKind.Sprite, s, dst, default, tint);

    public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) => Add(StatusBarCallKind.Clipped, s, dst, srcNormalized, tint);

    public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) => Add(StatusBarCallKind.NineSlice, s, dst, default, tint);

    public void Tile(in UiSprite s, UiRect dst, UiColor tint) => Add(StatusBarCallKind.Tile, s, dst, default, tint);

    public void Solid(UiRect dst, UiColor color)
    {
        Count++;
        if (Recording)
        {
            Calls.Add(new StatusBarCall(StatusBarCallKind.Solid, dst, default, color, null, null, default));
        }
    }

    public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) => Add(StatusBarCallKind.Mask, s, dst, default, fill);

    public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color)
    {
        Count++;
        if (Recording)
        {
            Calls.Add(new StatusBarCall(StatusBarCallKind.Text, default, default, color, null, text.ToString(), pos));
        }

        return MeasureText(text, style);
    }

    // 7 px per char at Body, 14 at Large.
    public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) => text.Length * (style == UiTextStyle.Large ? 14f : 7f);

    public void PushClip(UiRect r) => Count++;

    public void PopClip() => Count++;

    private void Add(StatusBarCallKind kind, in UiSprite s, UiRect dst, UiRect src, UiColor tint)
    {
        Count++;
        if (Recording)
        {
            string? name = _names!.TryGetValue(s, out string? n) ? n : null;
            Calls.Add(new StatusBarCall(kind, dst, src, tint, name, null, default));
        }
    }
}

public sealed class StatusBarFakeInput : IUiInput
{
    public UiPointer Pointer { get; set; }

    public int ClickedId { get; set; } = -1;

    public int HoveredId { get; set; } = -1;

    public int ActiveDragPayload { get; set; } = -1;

    public bool ModalActive { get; set; }

    public bool Button(int id, UiRect r) => id == ClickedId;

    public bool Hovered(int id, UiRect r) => id == HoveredId;

    public bool RightClicked(int id, UiRect r) => false;

    public bool BeginDrag(int id, UiRect r, int payload) => false;

    public bool TryEndDrag(out int payload, out Vector2 dropPosition)
    {
        payload = -1;
        dropPosition = default;
        return false;
    }
}

public sealed class StatusBarFakeActions : IHudActions
{
    public int CloseCount { get; private set; }

    public int InventoryCount { get; private set; }

    public void Inventory(InventoryHudAction action) => InventoryCount++;

    public void CloseInventory() => CloseCount++;
}

public static class StatusBarTestData
{
    private static UiSpriteTable? _table;

    public static UiSpriteTable Table
    {
        get
        {
            if (_table is null)
            {
                Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? t, out string error), error);
                _table = t;
            }

            return _table!;
        }
    }

    public static HudView View(int hp = 100, int maxHp = 100, int exp = 0, int toNext = 100, int level = 7, PlayerInteractionMode mode = PlayerInteractionMode.Combat) =>
        new()
        {
            Status = new HudStatusSnapshot(hp, maxHp, mode, 1, 2, 3, 1f, 4, level, exp, toNext)
        };
}
