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
