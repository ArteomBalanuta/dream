using System.Text;

namespace Dream.Rendering.Ui.Menu;

// The text of one single-line menu field. Typing and Backspace arrive from the window's text and key
// events; the widget (MenuWidgets.TextField) only draws it.
public sealed class MenuTextField
{
    public MenuTextField(int maxLength, bool digitsOnly = false, string? initial = null)
    {
        MaxLength = Math.Max(0, maxLength);
        DigitsOnly = digitsOnly;
        SetText(initial);
    }

    public int MaxLength { get; }

    public bool DigitsOnly { get; }

    // initial is filtered and truncated into this.
    public string Text { get; private set; } = string.Empty;

    public void SetText(string? text)
    {
        Text = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!Accepts(rune) || Text.Length + rune.Utf16SequenceLength > MaxLength)
            {
                continue;
            }

            Text += rune.ToString();
        }
    }

    // Appends one character; control characters, invalid runes, non-digits when DigitsOnly and overflow
    // past MaxLength are ignored. True when the text changed.
    public bool TypeCharacter(uint unicode)
    {
        if (unicode > int.MaxValue || !Rune.TryCreate((int)unicode, out Rune rune) || !Accepts(rune)
            || Text.Length + rune.Utf16SequenceLength > MaxLength)
        {
            return false;
        }

        Text += rune.ToString();
        return true;
    }

    // ---- caret and selection (CONTRACT-STUB: WS-A replaces every body in this block) ----
    // Indices are UTF-16 offsets into Text, never inside a surrogate pair. Caret is where typing goes;
    // SelectionAnchor is the other end of the selection (equal to Caret when nothing is selected).
    public int Caret => Text.Length;

    public int SelectionAnchor => Caret;

    public bool HasSelection => SelectionAnchor != Caret;

    public int SelectionStart => Math.Min(SelectionAnchor, Caret);

    public int SelectionEnd => Math.Max(SelectionAnchor, Caret);

    public string SelectedText => Text[SelectionStart..SelectionEnd];

    // The first UTF-16 index the widget draws (horizontal scroll). View state, owned by MenuWidgets.TextField.
    public int ScrollStart { get; set; }

    // Clamps and snaps index; extend keeps the anchor (a selection), otherwise the anchor follows.
    public void MoveCaret(int index, bool extend) => throw new NotImplementedException();

    // One rune, or one word when word is true (Ctrl): skips spaces/punctuation then a run of letters/digits.
    public void MoveLeft(bool extend, bool word) => throw new NotImplementedException();

    public void MoveRight(bool extend, bool word) => throw new NotImplementedException();

    public void MoveHome(bool extend) => throw new NotImplementedException();

    public void MoveEnd(bool extend) => throw new NotImplementedException();

    public void SelectAll() => throw new NotImplementedException();

    // Removes the selection, or the rune after the caret (the word after it when word). True when changed.
    public bool Delete(bool word = false) => throw new NotImplementedException();

    // Replaces the selection (or inserts at the caret) with text, filtered like typing: control characters
    // (newlines, tabs) are dropped, non-digits too when DigitsOnly, and what does not fit MaxLength is cut.
    // Returns the number of UTF-16 units inserted.
    public int Insert(string? text) => throw new NotImplementedException();

    // The selected text, removed; null when nothing is selected.
    public string? CutSelection() => throw new NotImplementedException();

    // Removes the selection, or the rune before the caret. (WS-A: also the word before it when word.)
    public bool Backspace(bool word)
    {
        return Backspace();
    }

    public bool Backspace()
    {
        if (Text.Length == 0)
        {
            return false;
        }

        int cut = Text.Length >= 2 && char.IsLowSurrogate(Text[^1]) && char.IsHighSurrogate(Text[^2]) ? 2 : 1;
        Text = Text[..^cut];
        return true;
    }

    private bool Accepts(Rune rune) =>
        !Rune.IsControl(rune) && (!DigitsOnly || rune.Value is >= '0' and <= '9');
}
