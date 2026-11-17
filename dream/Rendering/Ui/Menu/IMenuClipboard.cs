namespace Dream.Rendering.Ui.Menu;

// The system clipboard as the menu text fields see it. The window supplies the real one; tests a fake.
public interface IMenuClipboard
{
    // Null or empty when the clipboard holds no text.
    string? GetText();

    void SetText(string text);
}
