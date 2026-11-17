using Dream.Assets;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Menu;

// MenuWidgets.TextField on a recording canvas: scrolling, the selection highlight, the caret bar and the
// mouse. Tests marked [needs WS-A] call MenuTextField's real caret model (MoveCaret, SelectAll) and fail
// against the contract stubs until the branches are integrated.
public sealed class MenuTextFieldWidgetTests
{
    private static readonly UiColor SelectionColor = new(0xF0, 0xC4, 0x6C, 90);

    private readonly UiSpriteTable _table;
    private readonly MenuSkin _skin;
    private readonly UiInputRouter _router = new();
    private readonly MenuWidgets _widgets = new();
    private readonly UiRect _box = new(100f, 100f, 200f, 30f);

    public MenuTextFieldWidgetTests()
    {
        Assert.True(UiSpriteTable.TryLoadPages(AssetPaths.UiDirectory, MenuAtlasLoader.Pages, out UiSpriteTable? table, out string error), error);
        _table = table!;
        _skin = MenuSkin.From(_table);
    }

    private InventoryFakeCanvas Frame(MenuTextField field, Vector2 at, bool left, out bool result, bool focused = true, bool enabled = true, UiRect? rect = null)
    {
        InventoryFakeCanvas canvas = new(_table);
        UiLayout layout = UiLayout.Create(1440, 1024, 0f);
        _router.BeginFrame(new UiPointer(at, left, false, true));
        _widgets.Begin(canvas, _router, _skin, in layout);
        result = _widgets.TextField(7, rect ?? _box, field, focused, enabled);
        _router.EndFrame();
        return canvas;
    }

    private InventoryFakeCanvas Draw(MenuTextField field, bool focused = true, bool enabled = true, UiRect? rect = null) =>
        Frame(field, new Vector2(-1000f, -1000f), false, out _, focused, enabled, rect);

    private static int CaretBars(InventoryFakeCanvas canvas) =>
        canvas.Calls.Count(c => c.Kind == CanvasCallKind.Solid && c.Color == MenuWidgets.Gold);

    private static bool HasSelectionRect(InventoryFakeCanvas canvas) =>
        canvas.Calls.Any(c => c.Kind == CanvasCallKind.Solid && c.Color == SelectionColor);

    [Fact]
    public void ScrolledSelectAllThenType_DoesNotThrow()
    {
        MenuTextField field = new(253, initial: new string('m', 80));
        Draw(field);
        Assert.True(field.ScrollStart > 0);
        field.SelectAll();
        field.TypeCharacter('x');
        Draw(field);
        Assert.Equal("x", field.Text);
    }

    [Fact]
    public void BoxNarrowerThanAGlyph_DoesNotThrow()
    {
        MenuTextField field = new(64, initial: "ab\U0001F600cd");
        UiRect narrow = new(100f, 100f, 2f, 30f);
        Draw(field, rect: narrow);
        field.MoveHome(false);
        Frame(field, new Vector2(101f, 110f), true, out _, rect: narrow);
        field.SelectAll();
        field.TypeCharacter('x');
        Draw(field, rect: narrow);
    }

    [Fact]
    public void FocusedField_DrawsACaretBarNotAnUnderscore()
    {
        MenuTextField field = new(15, initial: "127.0.0.1");

        InventoryFakeCanvas canvas = Draw(field);

        Assert.Equal(1, CaretBars(canvas));
        Assert.DoesNotContain(canvas.Texts, t => t.Text.Contains('_'));
        Assert.True(canvas.HasText("127.0.0.1"));
    }

    [Fact]
    public void DisabledField_DrawsNoCaret()
    {
        MenuTextField field = new(15, initial: "127.0.0.1");

        InventoryFakeCanvas canvas = Draw(field, focused: true, enabled: false);

        Assert.Equal(0, CaretBars(canvas));
    }

    [Fact]
    public void UnfocusedField_DrawsNoCaret()
    {
        MenuTextField field = new(15, initial: "127.0.0.1");

        Assert.Equal(0, CaretBars(Draw(field, focused: false)));
    }

    [Fact]
    public void LongText_WithTheCaretAtTheEnd_Scrolls()
    {
        MenuTextField field = new(80, initial: new string('x', 60));

        InventoryFakeCanvas canvas = Draw(field);

        Assert.True(field.ScrollStart > 0);
        string drawn = canvas.Texts.Last().Text;
        Assert.True(drawn.Length < 60);
        Assert.EndsWith("x", drawn);
    }

    [Fact]
    public void ShortText_DoesNotScroll()
    {
        MenuTextField field = new(15, initial: "127.0.0.1");

        Draw(field);

        Assert.Equal(0, field.ScrollStart);
    }

    [Fact]
    public void DisabledField_IgnoresThePointer()
    {
        MenuTextField field = new(15, initial: "127.0.0.1");
        Vector2 at = new(_box.X + 2f, _box.Y + 10f);

        Frame(field, at, left: false, out _, focused: false, enabled: false);
        Frame(field, at, left: true, out bool pressed, focused: false, enabled: false);

        Assert.False(pressed);
    }

    [Fact]
    public void PressInsideTheBox_ReturnsTrueOnThePressFrame()
    {
        MenuTextField field = new(15, initial: "127.0.0.1");
        Vector2 at = new(_box.Right - 3f, _box.Y + 10f);

        Frame(field, at, left: false, out _);
        // [needs WS-A] the press calls field.MoveCaret.
        Frame(field, at, left: true, out bool pressed);

        Assert.True(pressed);
    }

    [Fact]
    public void PressAtTheRightEnd_MovesTheCaretToTheEnd()
    {
        MenuTextField field = new(15, initial: "127.0.0.1");
        field.MoveCaret(0, extend: false); // [needs WS-A]
        Vector2 at = new(_box.Right - 3f, _box.Y + 10f);

        Frame(field, at, left: false, out _);
        Frame(field, at, left: true, out _);

        Assert.Equal(9, field.Caret);
        Assert.False(field.HasSelection);
    }

    [Fact]
    public void PressNearTheStart_MovesTheCaretToZero()
    {
        MenuTextField field = new(15, initial: "127.0.0.1"); // caret starts at the end
        Vector2 at = new(_box.X + 2f, _box.Y + 10f);

        Frame(field, at, left: false, out _);
        Frame(field, at, left: true, out _); // [needs WS-A]

        Assert.Equal(0, field.Caret);
        Assert.Equal(0, field.ScrollStart);
    }

    [Fact]
    public void PressThenDrag_ExtendsTheSelection()
    {
        MenuTextField field = new(15, initial: "127.0.0.1");
        Vector2 start = new(_box.X + 4f, _box.Y + 10f);
        Vector2 end = new(_box.Right - 3f, _box.Y + 10f);

        Frame(field, start, left: false, out _);
        Frame(field, start, left: true, out _); // [needs WS-A]
        InventoryFakeCanvas dragged = Frame(field, end, left: true, out _);

        Assert.True(field.HasSelection);
        Assert.Equal(0, field.SelectionStart);
        Assert.Equal(9, field.SelectionEnd);
        Assert.True(HasSelectionRect(dragged));
    }

    [Fact]
    public void ReleaseEndsTheDrag()
    {
        MenuTextField field = new(15, initial: "127.0.0.1");
        Vector2 start = new(_box.X + 4f, _box.Y + 10f);
        Vector2 far = new(_box.Right - 3f, _box.Y + 10f);

        Frame(field, start, left: false, out _);
        Frame(field, start, left: true, out _); // [needs WS-A]
        Frame(field, start, left: false, out _);
        Frame(field, far, left: false, out _);

        Assert.False(field.HasSelection);
        Assert.Equal(0, field.Caret);
    }

    [Fact]
    public void LongText_WithTheCaretAtZero_DoesNotScroll()
    {
        MenuTextField field = new(80, initial: new string('x', 60));
        field.MoveCaret(0, extend: false); // [needs WS-A]

        Draw(field);

        Assert.Equal(0, field.ScrollStart);
    }

    [Fact]
    public void WithASelection_ATranslucentRectIsDrawn()
    {
        MenuTextField field = new(15, initial: "127.0.0.1");
        InventoryFakeCanvas plain = Draw(field);
        field.SelectAll(); // [needs WS-A]

        InventoryFakeCanvas selected = Draw(field);

        Assert.False(HasSelectionRect(plain));
        Assert.True(HasSelectionRect(selected));
    }

    [Fact]
    public void WithASelection_AndTheFieldDisabled_NoSelectionRectIsDrawn()
    {
        MenuTextField field = new(15, initial: "127.0.0.1");
        field.SelectAll(); // [needs WS-A]

        Assert.False(HasSelectionRect(Draw(field, focused: true, enabled: false)));
    }
}
