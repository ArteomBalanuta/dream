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
