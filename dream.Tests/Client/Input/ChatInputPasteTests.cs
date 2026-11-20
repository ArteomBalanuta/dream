using System.Linq;
using Xunit;
using Dream.Client.Input;
using Dream.Rendering.Ui.Menu;
using Dream.Rendering.Ui.Panels;

namespace Dream.Tests.Client.Input;

// Pasting into the chat line, which is also where /dev commands are typed. The point of the design is that
// a paste goes through TypeCharacter exactly as a keystroke does, so the plain-character filter and the
// length cap cannot be bypassed by the clipboard - the tests below are mostly proofs of that.
public sealed class ChatInputPasteTests
{
    private sealed class FakeClipboard(string? text) : IMenuClipboard
    {
        public string? Text { get; set; } = text;

        public int Reads { get; private set; }

        public string? GetText()
        {
            Reads++;
            return Text;
        }

        public bool SetText(string text)
        {
            Text = text;
            return true;
        }
    }

    private static (ChatInput Input, ChatHudState State) Open(string? clipboard)
    {
        ChatHudState state = new();
        ChatInput input = new(state) { Clipboard = new FakeClipboard(clipboard) };
        input.Open();
        return (input, state);
    }

    [Fact]
    public void Paste_PutsTheClipboardIntoTheOpenLine()
    {
        (ChatInput input, ChatHudState state) = Open("/dev spawn RisenSkeleton 1 4");

        Assert.Equal(28, input.Paste());
        Assert.Equal("/dev spawn RisenSkeleton 1 4", state.Input.ToString());
    }

    [Fact]
    public void Paste_AppendsAtTheCaret_SoItCombinesWithTyping()
    {
        (ChatInput input, ChatHudState state) = Open("RisenSkeleton");
        foreach (char c in "/dev spawn ")
        {
            input.TypeCharacter(c);
        }

        input.Paste();
        input.TypeCharacter(' ');
        input.TypeCharacter('2');

        Assert.Equal("/dev spawn RisenSkeleton 2", state.Input.ToString());
    }

    // The line is single-line, so a multi-line clipboard contributes its first real line and nothing else.
    // Without this a pasted block would either be rejected whole or smear several commands into one.
    [Theory]
    [InlineData("first\nsecond", "first")]
    [InlineData("first\r\nsecond", "first")]
    [InlineData("\n\n  padded  \nlater", "padded")]
    public void Paste_TakesTheFirstNonEmptyLine_Trimmed(string clipboard, string expected)
    {
        (ChatInput input, ChatHudState state) = Open(clipboard);

        input.Paste();

        Assert.Equal(expected, state.Input.ToString());
    }

    [Fact]
    public void Paste_IsIgnoredWhenTheLineIsClosed_AndNeverEvenReadsTheClipboard()
    {
        ChatHudState state = new();
        FakeClipboard clipboard = new("secret");
        ChatInput input = new(state) { Clipboard = clipboard };

        Assert.Equal(0, input.Paste());
        Assert.Equal(0, state.Input.Length);
        // Not merely discarded after the fact: a closed line must not touch the clipboard at all.
        Assert.Equal(0, clipboard.Reads);
    }

    [Fact]
    public void Paste_WithNoClipboardOrNoText_DoesNothingAndDoesNotThrow()
    {
        ChatHudState state = new();
        ChatInput input = new(state);
        input.Open();
        Assert.Equal(0, input.Paste());

        (ChatInput empty, ChatHudState emptyState) = Open(string.Empty);
        Assert.Equal(0, empty.Paste());
        Assert.Equal(0, emptyState.Input.Length);

        (ChatInput blank, ChatHudState blankState) = Open("   \n\t\n");
        Assert.Equal(0, blank.Paste());
        Assert.Equal(0, blankState.Input.Length);
    }

    // The two rules a paste must not be able to break, because the clipboard is attacker-ish input: it can
    // hold control characters and bidi overrides, and it can hold far more than the line allows.
    [Fact]
    public void Paste_FiltersTheSameCharactersTypingDoes()
    {
        (ChatInput input, ChatHudState state) = Open("ok‎bidi\u0007bell");

        input.Paste();

        Assert.Equal("okbidibell", state.Input.ToString());
        Assert.DoesNotContain('‎', state.Input.ToString());
        Assert.DoesNotContain('\u0007', state.Input.ToString());
    }

    [Fact]
    public void Paste_FillsWhatFits_AndNeverExceedsTheLineLimit()
    {
        int max = ChatHudState.MaxInputChars;
        (ChatInput input, ChatHudState state) = Open(new string('x', max + 50));

        int added = input.Paste();

        Assert.Equal(max, state.Input.Length);
        Assert.Equal(max, added);

        // A second paste on a full line adds nothing rather than throwing or wrapping.
        Assert.Equal(0, input.Paste());
        Assert.Equal(max, state.Input.Length);
    }

    [Fact]
    public void Paste_KeepsAstralCharactersWhole()
    {
        // U+1F480 SKULL: one rune, two UTF-16 units. It must arrive whole or not at all.
        (ChatInput input, ChatHudState state) = Open("a\U0001F480b");

        input.Paste();

        Assert.Equal("a\U0001F480b", state.Input.ToString());
        Assert.Equal(4, state.Input.Length);
    }

    // Everything above tests the logic with a fake. This pins the WIRING, which no test can instantiate
    // because Game owns a GLFW window: without these two lines the feature is dead in the shipped client
    // while every test in this file still passes. The repo pins the actor preload list the same way.
    [Fact]
    public void TheRealClipboard_IsWiredIntoThePlayScene()
    {
        string game = ReadRepoFile("dream", "Application", "Game.cs");
        Assert.Contains("_playScene.Clipboard = SystemClipboard;", game, StringComparison.Ordinal);
        // One clipboard for the window: the menu takes the same instance, so there is a single seam.
        Assert.Contains("SystemClipboard => _systemClipboard ??= new WindowClipboard(this)", game, StringComparison.Ordinal);

        string scene = ReadRepoFile("dream", "Client", "Scenes", "PlayScene.Replication.cs");
        Assert.Contains("set => _chatInput.Clipboard = value;", scene, StringComparison.Ordinal);

        // And the key that triggers it, which is likewise unreachable from a unit test.
        string chat = ReadRepoFile("dream", "Client", "Input", "ChatInput.cs");
        Assert.Contains("keyboard.IsKeyPressed(Keys.V)", chat, StringComparison.Ordinal);
        Assert.Contains("keyboard.IsKeyPressed(Keys.Insert)", chat, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "dream", "Rendering")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        return File.ReadAllText(Path.Combine(new[] { root! }.Concat(parts).ToArray()));
    }

    // The liveness check for this whole file: with no clipboard wired, the feature is absent and every
    // assertion above about content would be unreachable. This is what the bug looked like before the fix.
    [Fact]
    public void WithoutAClipboard_PastingIsANoOp_WhichIsTheBugThisFixed()
    {
        ChatHudState state = new();
        ChatInput input = new(state);
        input.Open();
        foreach (char c in "/dev ")
        {
            input.TypeCharacter(c);
        }

        Assert.Null(input.Clipboard);
        Assert.Equal(0, input.Paste());
        Assert.Equal("/dev ", state.Input.ToString());
    }
}
