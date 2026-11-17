using System.Text;

namespace Dream.Rendering.Ui.Menu;

// The text of one single-line menu field, with a caret and a selection. Typing and editing keys arrive from
// the window's text and key events; the widget (MenuWidgets.TextField) draws it and places the caret.
public sealed class MenuTextField
{
    private int _caret;
    private int _anchor;
    private int _scrollStart;

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

    // Caret at the end, nothing selected, scroll reset.
    public void SetText(string? text)
    {
        Text = string.IsNullOrEmpty(text) ? string.Empty : Filter(text, MaxLength);
        _caret = Text.Length;
        _anchor = _caret;
        _scrollStart = 0;
    }

    // Replaces the selection or inserts at the caret; control characters, invalid runes, non-digits when
    // DigitsOnly and overflow past MaxLength (counted after the selection is removed) are ignored.
    // True when the text changed.
    public bool TypeCharacter(uint unicode)
    {
        if (unicode > int.MaxValue || !Rune.TryCreate((int)unicode, out Rune rune) || !Accepts(rune)
            || Text.Length - (SelectionEnd - SelectionStart) + rune.Utf16SequenceLength > MaxLength)
        {
            return false;
        }

        Replace(SelectionStart, SelectionEnd, rune.ToString());
        return true;
    }

    // Indices are UTF-16 offsets into Text, never inside a surrogate pair. Caret is where typing goes;
    // SelectionAnchor is the other end of the selection (equal to Caret when nothing is selected).
    public int Caret => _caret;

    public int SelectionAnchor => _anchor;

    public bool HasSelection => _anchor != _caret;

    public int SelectionStart => Math.Min(_anchor, _caret);

    public int SelectionEnd => Math.Max(_anchor, _caret);

    public string SelectedText => Text[SelectionStart..SelectionEnd];

    // The first UTF-16 index the widget draws (horizontal scroll). View state, owned by MenuWidgets.TextField.
    public int ScrollStart
    {
        get => _scrollStart;
        set => _scrollStart = Math.Min(Snap(value), _caret);
    }

    // Clamps and snaps index; extend keeps the anchor (a selection), otherwise the anchor follows.
    public void MoveCaret(int index, bool extend)
    {
        _caret = Snap(index);
        _scrollStart = Math.Min(_scrollStart, _caret);
        if (!extend)
        {
            _anchor = _caret;
        }
    }

    // One rune, or one word when word is true (Ctrl): skips spaces/punctuation then a run of letters/digits.
    // Without extend, an existing selection collapses to the side moved toward.
    public void MoveLeft(bool extend, bool word)
    {
        if (!extend && HasSelection)
        {
            MoveCaret(SelectionStart, false);
            return;
        }

        MoveCaret(word ? WordLeft(_caret) : PrevRune(_caret), extend);
    }

    public void MoveRight(bool extend, bool word)
    {
        if (!extend && HasSelection)
        {
            MoveCaret(SelectionEnd, false);
            return;
        }

        MoveCaret(word ? WordRight(_caret) : NextRune(_caret), extend);
    }

    public void MoveHome(bool extend) => MoveCaret(0, extend);

    public void MoveEnd(bool extend) => MoveCaret(Text.Length, extend);

    public void SelectAll()
    {
        _anchor = 0;
        _caret = Text.Length;
    }

    // Removes the selection, or the rune after the caret (the word after it when word). True when changed.
    public bool Delete(bool word = false)
    {
        if (HasSelection)
        {
            Replace(SelectionStart, SelectionEnd, string.Empty);
            return true;
        }

        int end = word ? WordRight(_caret) : NextRune(_caret);
        if (end == _caret)
        {
            return false;
        }

        Replace(_caret, end, string.Empty);
        return true;
    }

    // Replaces the selection (or inserts at the caret) with text, filtered like typing: only the first line is taken, control characters
    // (tabs) are dropped, non-digits too when DigitsOnly, and what does not fit MaxLength is cut.
    // Returns the number of UTF-16 units inserted.
    public int Insert(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        text = FirstLine(text);
        int room = MaxLength - (Text.Length - (SelectionEnd - SelectionStart));
        string filtered = Filter(text, Math.Max(0, room));
        if (filtered.Length == 0)
        {
            return 0;
        }

        Replace(SelectionStart, SelectionEnd, filtered);
        return filtered.Length;
    }

    // The text up to the first line break; when it starts with line breaks, the first non-empty line.
    private static string FirstLine(string text)
    {
        foreach (string line in text.Split(['\r', '\n']))
        {
            if (line.Length > 0)
            {
                return line;
            }
        }

        return string.Empty;
    }

    // The selected text, removed; null when nothing is selected.
    public string? CutSelection()
    {
        if (!HasSelection)
        {
            return null;
        }

        string selected = SelectedText;
        Replace(SelectionStart, SelectionEnd, string.Empty);
        return selected;
    }

    // Removes the selection, or the rune before the caret (the word before it when word).
    public bool Backspace(bool word)
    {
        if (HasSelection)
        {
            Replace(SelectionStart, SelectionEnd, string.Empty);
            return true;
        }

        int start = word ? WordLeft(_caret) : PrevRune(_caret);
        if (start == _caret)
        {
            return false;
        }

        Replace(start, _caret, string.Empty);
        return true;
    }

    public bool Backspace() => Backspace(false);

    private void Replace(int start, int end, string with)
    {
        Text = string.Concat(Text.AsSpan(0, start), with, Text.AsSpan(end));
        _caret = start + with.Length;
        _anchor = _caret;
        _scrollStart = Math.Min(Snap(_scrollStart), _caret);
    }

    private string Filter(string text, int room)
    {
        StringBuilder sb = new();
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!Accepts(rune))
            {
                continue;
            }

            if (sb.Length + rune.Utf16SequenceLength > room)
            {
                break;
            }

            sb.Append(rune.ToString());
        }

        return sb.ToString();
    }

    private int Snap(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        if (index > 0 && index < Text.Length && char.IsLowSurrogate(Text[index]) && char.IsHighSurrogate(Text[index - 1]))
        {
            index--;
        }

        return index;
    }

    private int PrevRune(int i) => i <= 0 ? 0 : Snap(i - 1);

    private int NextRune(int i)
    {
        if (i >= Text.Length)
        {
            return Text.Length;
        }

        return char.IsHighSurrogate(Text[i]) && i + 1 < Text.Length && char.IsLowSurrogate(Text[i + 1]) ? i + 2 : i + 1;
    }

    private bool IsWordAt(int i) => char.IsLetterOrDigit(Text, i);

    private int WordLeft(int i)
    {
        while (i > 0 && !IsWordAt(PrevRune(i)))
        {
            i = PrevRune(i);
        }

        while (i > 0 && IsWordAt(PrevRune(i)))
        {
            i = PrevRune(i);
        }

        return i;
    }

    private int WordRight(int i)
    {
        while (i < Text.Length && IsWordAt(i))
        {
            i = NextRune(i);
        }

        while (i < Text.Length && !IsWordAt(i))
        {
            i = NextRune(i);
        }

        return i;
    }

    private bool Accepts(Rune rune) =>
        !Rune.IsControl(rune) && (!DigitsOnly || rune.Value is >= '0' and <= '9');
}
