namespace Dream.Rendering.Ui.Menu;

// The text of one single-line menu field. Typing and Backspace arrive from the window's text and key
// events; the widget (MenuWidgets.TextField) only draws it.
// CONTRACT-STUB: WS-A replaces the bodies.
public sealed class MenuTextField
{
    public MenuTextField(int maxLength, bool digitsOnly = false, string? initial = null)
    {
        MaxLength = maxLength;
        DigitsOnly = digitsOnly;
    }

    public int MaxLength { get; }

    public bool DigitsOnly { get; }

    // initial is filtered and truncated into this.
    public string Text { get; private set; } = string.Empty;

    public void SetText(string? text) => throw new NotImplementedException();

    // Appends one character; control characters, invalid runes, non-digits when DigitsOnly and overflow
    // past MaxLength are ignored. True when the text changed.
    public bool TypeCharacter(uint unicode) => throw new NotImplementedException();

    public bool Backspace() => throw new NotImplementedException();
}
