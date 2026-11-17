using Dream.Rendering.Ui.Menu;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Menu;

public sealed class MenuTextFieldTests
{
    private static void Type(MenuTextField f, string s)
    {
        foreach (char c in s)
        {
            f.TypeCharacter(c);
        }
    }

    private sealed class FakeClipboard : IMenuClipboard
    {
        public string? Text { get; set; }

        public string? GetText() => Text;

        public bool SetText(string text)
        {
            Text = text;
            return true;
        }
    }

    private static MenuTextField Field(string text, int max = 64, bool digits = false) => new(max, digits, text);

    [Fact]
    public void Insert_InTheMiddleAndTypingReplacesSelection()
    {
        MenuTextField f = Field("abcd");
        f.MoveCaret(2, false);
        f.TypeCharacter('X');
        Assert.Equal("abXcd", f.Text);
        Assert.Equal(3, f.Caret);

        f.MoveCaret(1, false);
        f.MoveCaret(4, true);
        f.TypeCharacter('Y');
        Assert.Equal("aYd", f.Text);
        Assert.False(f.HasSelection);
    }

    [Fact]
    public void MaxLength_CountsTextAfterSelectionRemoval()
    {
        MenuTextField f = Field("abc", 3);
        Assert.False(f.TypeCharacter('z'));
        f.SelectAll();
        Assert.True(f.TypeCharacter('z'));
        Assert.Equal("z", f.Text);
    }

    [Fact]
    public void BackspaceAndDelete_WithAndWithoutSelection()
    {
        MenuTextField f = Field("abcd");
        f.MoveCaret(2, false);
        Assert.True(f.Backspace());
        Assert.Equal("acd", f.Text);
        Assert.True(f.Delete());
        Assert.Equal("ad", f.Text);
        f.SelectAll();
        Assert.True(f.Delete());
        Assert.Equal(string.Empty, f.Text);
        Assert.False(f.Delete());
        Assert.False(f.Backspace());

        f.SetText("abcd");
        f.MoveCaret(1, false);
        f.MoveCaret(3, true);
        Assert.True(f.Backspace());
        Assert.Equal("ad", f.Text);
        Assert.Equal(1, f.Caret);
    }

    [Fact]
    public void WordMovesAndDeletes()
    {
        MenuTextField f = Field("play er.name 12");
        f.MoveLeft(false, true);
        Assert.Equal(13, f.Caret);
        f.MoveLeft(false, true);
        Assert.Equal(8, f.Caret);
        f.MoveLeft(false, true);
        Assert.Equal(5, f.Caret);
        f.MoveRight(false, true);
        Assert.Equal(8, f.Caret);
        f.MoveRight(false, true);
        Assert.Equal(13, f.Caret);

        f.Backspace(true);
        Assert.Equal("play er.12", f.Text);
        f.SetText("play er.name 12");
        f.MoveHome(false);
        f.Delete(true);
        Assert.Equal("er.name 12", f.Text);
        f.MoveEnd(false);
        f.Backspace(true);
        Assert.Equal("er.name ", f.Text);
        f.Backspace(true);
        Assert.Equal("er.", f.Text);
    }

    [Fact]
    public void ShiftExtendsThenPlainMoveCollapsesTowardTheSide()
    {
        MenuTextField f = Field("abcdef");
        f.MoveCaret(3, false);
        f.MoveLeft(true, false);
        f.MoveLeft(true, false);
        Assert.Equal(1, f.Caret);
        Assert.Equal(3, f.SelectionAnchor);
        Assert.Equal("bc", f.SelectedText);
        f.MoveRight(false, false);
        Assert.Equal(3, f.Caret);
        Assert.False(f.HasSelection);

        f.MoveCaret(1, true);
        f.MoveLeft(false, false);
        Assert.Equal(1, f.Caret);
        Assert.False(f.HasSelection);
    }

    [Fact]
    public void HomeEndAndSelectAll()
    {
        MenuTextField f = Field("abcd");
        f.MoveCaret(2, false);
        f.MoveHome(true);
        Assert.Equal("ab", f.SelectedText);
        f.MoveCaret(2, false);
        f.MoveEnd(true);
        Assert.Equal("cd", f.SelectedText);
        f.MoveHome(false);
        f.SelectAll();
        Assert.Equal("abcd", f.SelectedText);
    }

    [Fact]
    public void Insert_FiltersAndTruncates()
    {
        MenuTextField name = Field("", 6);
        Assert.Equal(6, name.Insert("ab\tcdefgh"));
        Assert.Equal("abcdef", name.Text);
        Assert.Equal(0, name.Insert("zz"));
        Assert.Equal(0, name.Insert(null));

        MenuTextField port = Field("", 5, digits: true);
        Assert.Equal(5, port.Insert("1a2 34-5678"));
        Assert.Equal("12345", port.Text);

        name.SelectAll();
        Assert.Equal(2, name.Insert("xy"));
        Assert.Equal("xy", name.Text);
    }

    [Fact]
    public void Insert_TakesOnlyTheFirstNonEmptyLine()
    {
        MenuTextField f = Field("");
        Assert.Equal(2, f.Insert("ab\r\ncd"));
        Assert.Equal("ab", f.Text);
        f.SetText("");
        Assert.Equal(2, f.Insert("\r\n\ncd\nef"));
        Assert.Equal("cd", f.Text);
    }

    [Fact]
    public void Filter_StopsAtTheFirstRuneThatDoesNotFit()
    {
        Assert.Equal("abc", new MenuTextField(4, initial: "abc\U0001F600d").Text);
        MenuTextField f = Field("", 4);
        Assert.Equal(3, f.Insert("abc\U0001F600d"));
    }

    [Fact]
    public void ScrolledSelectAllThenTypeKeepsScrollStartAtMostCaret()
    {
        MenuTextField f = Field(new string('a', 40));
        f.ScrollStart = 30;
        f.SelectAll();
        f.TypeCharacter('x');
        Assert.Equal("x", f.Text);
        Assert.True(f.ScrollStart <= f.Caret);
    }

    [Fact]
    public void Form_CutKeepsTheTextWhenTheClipboardCannotBeWritten()
    {
        MultiplayerFormState form = new("name", "abcdef", 1, 2);
        form.Address.SelectAll();
        form.HandleKey(MenuKey.Cut, MenuKeyModifiers.Control, new FailingClipboard());
        Assert.Equal("abcdef", form.Address.Text);
        form.HandleKey(MenuKey.Cut, MenuKeyModifiers.Control, null);
        Assert.Equal("abcdef", form.Address.Text);
    }

    private sealed class FailingClipboard : IMenuClipboard
    {
        public string? GetText() => null;

        public bool SetText(string text) => false;
    }

    [Fact]
    public void Form_PastingHostAndPortSplitsThem_ButIpv6IsPastedAsIs()
    {
        FakeClipboard clip = new() { Text = " example.com:7777 \r\nnext" };
        MultiplayerFormState form = new("name", "old", 1, 2);
        form.Address.SelectAll();
        form.HandleKey(MenuKey.Paste, MenuKeyModifiers.Control, clip);
        Assert.Equal("example.com", form.Address.Text);
        Assert.Equal("7777", form.JoinPort.Text);

        clip.Text = "::1";
        form.Address.SelectAll();
        form.HandleKey(MenuKey.Paste, MenuKeyModifiers.Control, clip);
        Assert.Equal("::1", form.Address.Text);
        Assert.Equal("7777", form.JoinPort.Text);

        clip.Text = "host:123456";
        form.Address.SelectAll();
        form.HandleKey(MenuKey.Paste, MenuKeyModifiers.Control, clip);
        Assert.Equal("host:123456", form.Address.Text);
    }

    [Fact]
    public void MapMenuKey_AltGrIsNotAShortcut()
    {
        Assert.Equal(MenuKey.SelectAll, Dream.Application.Game.MapMenuKey(OpenTK.Windowing.GraphicsLibraryFramework.Keys.A, true, false, false));
        Assert.Null(Dream.Application.Game.MapMenuKey(OpenTK.Windowing.GraphicsLibraryFramework.Keys.A, true, true, false));
    }

    [Fact]
    public void CutSelection_ReturnsAndRemoves()
    {
        MenuTextField f = Field("abcd");
        Assert.Null(f.CutSelection());
        f.MoveCaret(1, false);
        f.MoveCaret(3, true);
        Assert.Equal("bc", f.CutSelection());
        Assert.Equal("ad", f.Text);
        Assert.Equal(1, f.Caret);
    }

    [Fact]
    public void SurrogatePairs_NeverSplit()
    {
        MenuTextField f = Field("a\U0001F600b");
        f.MoveCaret(2, false);
        Assert.Equal(1, f.Caret);
        f.MoveRight(false, false);
        Assert.Equal(3, f.Caret);
        f.MoveLeft(false, false);
        Assert.Equal(1, f.Caret);
        f.MoveCaret(99, false);
        Assert.Equal(4, f.Caret);
        f.MoveLeft(false, false);
        f.MoveLeft(false, false);
        Assert.Equal(1, f.Caret);
        Assert.True(f.Delete());
        Assert.Equal("ab", f.Text);
        f.SetText("a\U0001F600b");
        f.MoveCaret(3, false);
        f.Backspace();
        Assert.Equal("ab", f.Text);
    }

    [Fact]
    public void ScrollStart_IsClampedWhenTextShrinks()
    {
        MenuTextField f = Field("abcdef");
        f.ScrollStart = 4;
        f.SelectAll();
        f.Delete();
        Assert.Equal(0, f.ScrollStart);
        f.SetText("abc");
        Assert.Equal(0, f.ScrollStart);
    }

    [Fact]
    public void Form_TabSelectsAll_ShiftTabGoesBackwards()
    {
        MultiplayerFormState form = new("name", "addr", 1, 2);
        form.HandleKey(MenuKey.Tab, MenuKeyModifiers.None, null);
        Assert.Equal(MultiplayerField.JoinPort, form.Focused);
        Assert.Equal("1", form.JoinPort.SelectedText);
        form.HandleKey(MenuKey.Tab, MenuKeyModifiers.Shift, null);
        Assert.Equal(MultiplayerField.Address, form.Focused);
        Assert.Equal("addr", form.Address.SelectedText);
        form.Focused = MultiplayerField.PlayerName;
        form.HandleKey(MenuKey.Tab, MenuKeyModifiers.Shift, null);
        Assert.Equal(MultiplayerField.HostPort, form.Focused);
    }

    [Fact]
    public void Form_CopyCutPasteThroughClipboard()
    {
        FakeClipboard clip = new();
        MultiplayerFormState form = new("name", "abcdef", 1, 2);
        form.Address.MoveCaret(1, false);
        form.Address.MoveCaret(3, true);

        form.HandleKey(MenuKey.Copy, MenuKeyModifiers.Control, clip);
        Assert.Equal("bc", clip.Text);
        Assert.Equal("abcdef", form.Address.Text);

        clip.Text = null;
        form.HandleKey(MenuKey.Cut, MenuKeyModifiers.Control, clip);
        Assert.Equal("bc", clip.Text);
        Assert.Equal("adef", form.Address.Text);

        form.HandleKey(MenuKey.Paste, MenuKeyModifiers.Control, clip);
        Assert.Equal("abcdef", form.Address.Text);
        Assert.Equal(3, form.Address.Caret);
    }

    [Fact]
    public void Form_PasteWithNullClipboardIsNoOp_AndEmptyCopyLeavesClipboard()
    {
        MultiplayerFormState form = new("name", "abc", 1, 2);
        form.HandleKey(MenuKey.Paste, MenuKeyModifiers.Control, null);
        Assert.Equal("abc", form.Address.Text);

        FakeClipboard clip = new() { Text = "keep" };
        form.HandleKey(MenuKey.Copy, MenuKeyModifiers.Control, clip);
        Assert.Equal("keep", clip.Text);
    }

    [Fact]
    public void Form_ModifierKeysEditTheFocusedField()
    {
        MultiplayerFormState form = new("name", "one two", 1, 2);
        form.HandleKey(MenuKey.Backspace, MenuKeyModifiers.Control, null);
        Assert.Equal("one ", form.Address.Text);
        form.HandleKey(MenuKey.Home, MenuKeyModifiers.None, null);
        form.HandleKey(MenuKey.Right, MenuKeyModifiers.Shift, null);
        Assert.Equal("o", form.Address.SelectedText);
        form.HandleKey(MenuKey.Delete, MenuKeyModifiers.None, null);
        Assert.Equal("ne ", form.Address.Text);
    }

    [Fact]
    public void DigitsOnly_IgnoresLettersAndNonAsciiDigits()
    {
        MenuTextField f = new(5, digitsOnly: true);
        Type(f, "a1b2-3٣");

        Assert.Equal("123", f.Text);
        Assert.False(f.TypeCharacter('x'));
    }

    [Fact]
    public void MaxLength_IgnoresOverflow()
    {
        MenuTextField f = new(3);
        Type(f, "abcdef");

        Assert.Equal("abc", f.Text);
        Assert.False(f.TypeCharacter('z'));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(8u)]
    [InlineData(9u)]
    [InlineData(13u)]
    [InlineData(27u)]
    [InlineData(127u)]
    [InlineData(0xD800u)]
    [InlineData(0x110000u)]
    public void ControlAndInvalidCharacters_AreIgnored(uint code)
    {
        MenuTextField f = new(8);

        Assert.False(f.TypeCharacter(code));
        Assert.Equal(string.Empty, f.Text);
    }

    [Fact]
    public void TypeCharacter_ReturnsTrueWhenChangedAndAcceptsSupplementaryRunes()
    {
        MenuTextField f = new(4);

        Assert.True(f.TypeCharacter('a'));
        Assert.True(f.TypeCharacter(0x1F600));
        Assert.Equal("a\U0001F600", f.Text);
        Assert.True(f.Backspace());
        Assert.Equal("a", f.Text);
    }

    [Fact]
    public void Backspace_OnEmptyReturnsFalse()
    {
        MenuTextField f = new(4);

        Assert.False(f.Backspace());
        f.TypeCharacter('a');
        Assert.True(f.Backspace());
        Assert.False(f.Backspace());
    }

    [Fact]
    public void Initial_IsFilteredAndTruncated()
    {
        Assert.Equal("abcd", new MenuTextField(4, initial: "abcdefgh").Text);
        Assert.Equal("123", new MenuTextField(5, digitsOnly: true, initial: "1a2b3").Text);
        Assert.Equal("ab", new MenuTextField(5, initial: "a\tb").Text);
        Assert.Equal(string.Empty, new MenuTextField(5, initial: null).Text);
    }

    [Fact]
    public void Form_FillsFieldsAndDefaultsToAddress()
    {
        MultiplayerFormState form = new("Wanderer", "example.org", 7777, 7778);

        Assert.Equal("Wanderer", form.PlayerName.Text);
        Assert.Equal("example.org", form.Address.Text);
        Assert.Equal("7777", form.JoinPort.Text);
        Assert.Equal("7778", form.HostPort.Text);
        Assert.Equal(MultiplayerField.Address, form.Focused);
        Assert.Same(form.Address, form.FocusedField);
    }

    [Fact]
    public void Form_FocusedFieldMapsTheEnum()
    {
        MultiplayerFormState form = new("n", "a", 1, 2);

        form.Focused = MultiplayerField.PlayerName;
        Assert.Same(form.PlayerName, form.FocusedField);
        form.Focused = MultiplayerField.JoinPort;
        Assert.Same(form.JoinPort, form.FocusedField);
        form.Focused = MultiplayerField.HostPort;
        Assert.Same(form.HostPort, form.FocusedField);
        form.Focused = MultiplayerField.None;
        Assert.Null(form.FocusedField);
    }

    [Fact]
    public void Form_TypingAndBackspaceGoToTheFocusedField()
    {
        MultiplayerFormState form = new("n", "a", 1, 2);
        form.TypeCharacter('b');
        form.Focused = MultiplayerField.JoinPort;
        form.TypeCharacter('9');
        form.TypeCharacter('x');

        Assert.Equal("ab", form.Address.Text);
        Assert.Equal("19", form.JoinPort.Text);
        Assert.Equal(MenuAction.None, form.HandleKey(MenuKey.Backspace));
        Assert.Equal("1", form.JoinPort.Text);
        Assert.Equal("ab", form.Address.Text);

        form.Focused = MultiplayerField.None;
        form.TypeCharacter('q');
        Assert.Equal(MenuAction.None, form.HandleKey(MenuKey.Backspace));
        Assert.Equal("ab", form.Address.Text);
    }

    [Fact]
    public void Form_TabCycles()
    {
        MultiplayerFormState form = new("n", "a", 1, 2) { Focused = MultiplayerField.PlayerName };
        MultiplayerField[] expected =
        [
            MultiplayerField.Address, MultiplayerField.JoinPort, MultiplayerField.HostPort, MultiplayerField.PlayerName
        ];
        foreach (MultiplayerField next in expected)
        {
            Assert.Equal(MenuAction.None, form.HandleKey(MenuKey.Tab));
            Assert.Equal(next, form.Focused);
        }

        form.Focused = MultiplayerField.None;
        form.HandleKey(MenuKey.Tab);
        Assert.Equal(MultiplayerField.PlayerName, form.Focused);
    }

    [Theory]
    [InlineData(MultiplayerField.Address, MenuAction.Join)]
    [InlineData(MultiplayerField.JoinPort, MenuAction.Join)]
    [InlineData(MultiplayerField.HostPort, MenuAction.Host)]
    [InlineData(MultiplayerField.PlayerName, MenuAction.Join)]
    [InlineData(MultiplayerField.None, MenuAction.None)]
    public void Form_EnterMapsToJoinOrHost(MultiplayerField focus, MenuAction expected)
    {
        MultiplayerFormState form = new("n", "a", 1, 2) { Focused = focus };

        Assert.Equal(expected, form.HandleKey(MenuKey.Enter));
    }

    [Fact]
    public void Form_EscapeIsBack()
    {
        Assert.Equal(MenuAction.Back, new MultiplayerFormState("n", "a", 1, 2).HandleKey(MenuKey.Escape));
    }
}
